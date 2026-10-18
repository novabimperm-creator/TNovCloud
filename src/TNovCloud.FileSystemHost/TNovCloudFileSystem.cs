using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using Fsp;
using FileInfo = Fsp.Interop.FileInfo;
using VolumeInfo = Fsp.Interop.VolumeInfo;
using WinFspHost = Fsp.FileSystemHost;

namespace TNovCloud.FileSystemHost;

internal sealed class TNovCloudFileSystem(
    ManifestStore store,
    CloudApi api,
    Guid currentUserId,
    Action<string> log) : FileSystemBase
{
    private const uint AllocationUnit = 4096;
    private const uint WriteAccessMask =
        0x40000000 | // GENERIC_WRITE
        0x00000002 | // FILE_WRITE_DATA
        0x00000004 | // FILE_APPEND_DATA
        0x00000100;  // FILE_WRITE_ATTRIBUTES
    private const uint DeleteAccessMask = 0x00010000;

    private static readonly byte[] SecurityDescriptor = CreateSecurityDescriptor();

    public override int ExceptionHandler(Exception exception)
    {
        log(exception.ToString());
        return exception switch
        {
            UnauthorizedAccessException => STATUS_ACCESS_DENIED,
            HttpRequestException => STATUS_ACCESS_DENIED,
            FileNotFoundException => STATUS_OBJECT_NAME_NOT_FOUND,
            DirectoryNotFoundException => STATUS_OBJECT_PATH_NOT_FOUND,
            ArgumentException => STATUS_INVALID_PARAMETER,
            IOException => STATUS_UNEXPECTED_IO_ERROR,
            _ => STATUS_UNEXPECTED_IO_ERROR
        };
    }

    public override int Init(object hostObject)
    {
        var host = (WinFspHost)hostObject;
        host.SectorSize = (ushort)AllocationUnit;
        host.SectorsPerAllocationUnit = 1;
        host.MaxComponentLength = 255;
        host.FileInfoTimeout = 1000;
        host.DirInfoTimeout = 1000;
        host.VolumeInfoTimeout = 1000;
        host.CaseSensitiveSearch = false;
        host.CasePreservedNames = true;
        host.UnicodeOnDisk = true;
        host.PersistentAcls = true;
        host.PostCleanupWhenModifiedOnly = false;
        host.PassQueryDirectoryPattern = true;
        host.PassQueryDirectoryFileName = true;
        host.FlushAndPurgeOnCleanup = true;
        host.FileSystemName = "TNovCloud";
        host.VolumeCreationTime = (ulong)DateTime.UtcNow.ToFileTimeUtc();
        host.VolumeSerialNumber = 0x544E4F56;
        return STATUS_SUCCESS;
    }

    public override int GetVolumeInfo(out VolumeInfo volumeInfo)
    {
        volumeInfo = default;
        const ulong total = 1024UL * 1024 * 1024 * 1024;
        var used = GetCacheSize(HostPaths.CacheRoot);
        volumeInfo.TotalSize = total;
        volumeInfo.FreeSize = total > used ? total - used : 0;
        volumeInfo.SetVolumeLabel("TNovCloud");
        return STATUS_SUCCESS;
    }

    public override int SetVolumeLabel(string volumeLabel, out VolumeInfo volumeInfo)
        => GetVolumeInfo(out volumeInfo);

    public override int GetSecurityByName(
        string fileName,
        out uint fileAttributes,
        ref byte[] securityDescriptor)
    {
        var entry = store.Get(fileName);
        if (entry is null || entry.State == EntryState.Deleted)
        {
            fileAttributes = 0;
            return STATUS_OBJECT_NAME_NOT_FOUND;
        }

        fileAttributes = Attributes(entry);
        if (securityDescriptor is not null)
            securityDescriptor = SecurityDescriptor;
        return STATUS_SUCCESS;
    }

    public override int Create(
        string fileName,
        uint createOptions,
        uint grantedAccess,
        uint fileAttributes,
        byte[] securityDescriptor,
        ulong allocationSize,
        out object fileNode,
        out object fileDesc,
        out FileInfo fileInfo,
        out string normalizedName)
    {
        fileNode = null!;
        fileDesc = null!;
        fileInfo = default;
        normalizedName = null!;

        var isFolder = 0 != (createOptions & FILE_DIRECTORY_FILE);
        var path = ManifestStore.Normalize(fileName);
        ManifestEntry entry;
        try
        {
            entry = store.CreateLocal(path, isFolder);
        }
        catch (IOException) when (store.Get(path) is not null)
        {
            return STATUS_OBJECT_NAME_COLLISION;
        }

        if (!isFolder && allocationSize > 0)
        {
            using var allocation = OpenStream(path, true);
            allocation.SetLength(checked((long)allocationSize));
            store.MarkDirty(path);
            entry = store.Get(path)!;
        }

        if (isFolder)
        {
            try
            {
                api.CreateDirectoryAsync(path, CancellationToken.None).GetAwaiter().GetResult();
                store.MarkUploaded(path);
                entry = store.Get(path)!;
            }
            catch (Exception exception)
            {
                log($"Directory '{path}' is cached locally and pending upload: {exception.Message}");
            }
        }

        var handle = OpenHandle(entry, true);
        fileNode = handle;
        fileDesc = handle;
        FillInfo(entry, out fileInfo);
        normalizedName = "\\" + path.Replace('/', '\\');
        return STATUS_SUCCESS;
    }

    public override int Open(
        string fileName,
        uint createOptions,
        uint grantedAccess,
        out object fileNode,
        out object fileDesc,
        out FileInfo fileInfo,
        out string normalizedName)
    {
        fileNode = null!;
        fileDesc = null!;
        fileInfo = default;
        normalizedName = null!;

        var entry = store.Get(fileName);
        if (entry is null || entry.State == EntryState.Deleted)
            return STATUS_OBJECT_NAME_NOT_FOUND;
        if (0 != (createOptions & FILE_DIRECTORY_FILE) && !entry.IsFolder)
            return STATUS_NOT_A_DIRECTORY;
        if (0 != (createOptions & FILE_NON_DIRECTORY_FILE) && entry.IsFolder)
            return STATUS_FILE_IS_A_DIRECTORY;
        if (0 != (grantedAccess & WriteAccessMask) && !IsWritable(entry))
            return STATUS_ACCESS_DENIED;
        if (0 != (grantedAccess & DeleteAccessMask) && entry.Path.Length == 0)
            return STATUS_ACCESS_DENIED;

        var handle = OpenHandle(entry, IsWritable(entry));
        fileNode = handle;
        fileDesc = handle;
        FillInfo(entry, out fileInfo);
        normalizedName = "\\" + entry.Path.Replace('/', '\\');
        return STATUS_SUCCESS;
    }

    public override int Overwrite(
        object fileNode,
        object fileDesc,
        uint fileAttributes,
        bool replaceFileAttributes,
        ulong allocationSize,
        out FileInfo fileInfo)
    {
        var handle = Handle(fileDesc);
        if (!EnsureWritable(handle, out var entry, out var status))
        {
            fileInfo = default;
            return status;
        }
        if (entry.IsFolder)
        {
            fileInfo = default;
            return STATUS_FILE_IS_A_DIRECTORY;
        }

        handle.Stream!.SetLength(0);
        store.MarkDirty(handle.Path);
        FillInfo(store.Get(handle.Path)!, out fileInfo);
        return STATUS_SUCCESS;
    }

    public override int Read(
        object fileNode,
        object fileDesc,
        IntPtr buffer,
        ulong offset,
        uint length,
        out uint bytesTransferred)
    {
        var handle = Handle(fileDesc);
        bytesTransferred = 0;
        if (handle.Stream is null)
            return STATUS_FILE_IS_A_DIRECTORY;
        if (offset >= (ulong)handle.Stream.Length)
            return STATUS_END_OF_FILE;

        var count = (int)Math.Min(length, (ulong)handle.Stream.Length - offset);
        var bytes = new byte[count];
        handle.Stream.Position = checked((long)offset);
        var read = handle.Stream.Read(bytes, 0, count);
        Marshal.Copy(bytes, 0, buffer, read);
        bytesTransferred = (uint)read;
        return STATUS_SUCCESS;
    }

    public override int Write(
        object fileNode,
        object fileDesc,
        IntPtr buffer,
        ulong offset,
        uint length,
        bool writeToEndOfFile,
        bool constrainedIo,
        out uint bytesTransferred,
        out FileInfo fileInfo)
    {
        var handle = Handle(fileDesc);
        bytesTransferred = 0;
        if (!EnsureWritable(handle, out var entry, out var status))
        {
            fileInfo = default;
            return status;
        }
        if (handle.Stream is null)
        {
            fileInfo = default;
            return STATUS_FILE_IS_A_DIRECTORY;
        }

        if (writeToEndOfFile)
            offset = (ulong)handle.Stream.Length;
        if (constrainedIo)
        {
            if (offset >= (ulong)handle.Stream.Length)
            {
                FillInfo(entry, out fileInfo);
                return STATUS_SUCCESS;
            }
            length = (uint)Math.Min(length, (ulong)handle.Stream.Length - offset);
        }

        var bytes = new byte[checked((int)length)];
        Marshal.Copy(buffer, bytes, 0, bytes.Length);
        handle.Stream.Position = checked((long)offset);
        handle.Stream.Write(bytes);
        handle.Modified = true;
        store.MarkDirty(handle.Path);
        bytesTransferred = length;
        FillInfo(store.Get(handle.Path)!, out fileInfo);
        return STATUS_SUCCESS;
    }

    public override int Flush(object fileNode, object fileDesc, out FileInfo fileInfo)
    {
        if (fileDesc is not FileHandle handle)
        {
            fileInfo = default;
            return STATUS_SUCCESS;
        }

        handle.Stream?.Flush(true);
        var entry = store.Get(handle.Path);
        if (entry is null)
        {
            fileInfo = default;
            return STATUS_OBJECT_NAME_NOT_FOUND;
        }
        FillInfo(entry, out fileInfo);
        return STATUS_SUCCESS;
    }

    public override void Cleanup(
        object fileNode,
        object fileDesc,
        string fileName,
        uint flags)
    {
        var handle = Handle(fileDesc);
        handle.Stream?.Flush(true);

        if (0 != (flags & CleanupDelete))
        {
            handle.Stream?.Dispose();
            handle.Stream = null;
            var entryToDelete = store.Get(handle.Path);
            try
            {
                if (entryToDelete?.IsRemote == true)
                {
                    api.DeleteAsync(handle.Path, CancellationToken.None).GetAwaiter().GetResult();
                    store.DeleteRemoteConfirmed(handle.Path);
                }
                else
                    store.DeleteLocal(handle.Path);
                handle.Deleted = true;
            }
            catch (Exception exception)
            {
                log($"Delete of '{handle.Path}' was rejected; local entry retained: {exception.Message}");
                throw;
            }
            return;
        }

        var entry = store.Get(handle.Path);
        if (entry is { IsFolder: false, State: EntryState.New })
        {
            try
            {
                api.UploadNewAsync(entry.Path, store.CachePath(entry.Path), CancellationToken.None)
                    .GetAwaiter().GetResult();
                store.MarkUploaded(entry.Path);
            }
            catch (Exception exception)
            {
                log($"File '{entry.Path}' remains pending upload: {exception.Message}");
            }
        }
    }

    public override void Close(object fileNode, object fileDesc)
    {
        var handle = Handle(fileDesc);
        handle.Stream?.Dispose();
        handle.Stream = null;
    }

    public override int GetFileInfo(object fileNode, object fileDesc, out FileInfo fileInfo)
    {
        var handle = Handle(fileDesc);
        var entry = store.Get(handle.Path);
        if (entry is null)
        {
            fileInfo = default;
            return STATUS_OBJECT_NAME_NOT_FOUND;
        }
        FillInfo(entry, out fileInfo);
        return STATUS_SUCCESS;
    }

    public override int SetBasicInfo(
        object fileNode,
        object fileDesc,
        uint fileAttributes,
        ulong creationTime,
        ulong lastAccessTime,
        ulong lastWriteTime,
        ulong changeTime,
        out FileInfo fileInfo)
    {
        var handle = Handle(fileDesc);
        if (!EnsureWritable(handle, out _, out var status))
        {
            fileInfo = default;
            return status;
        }

        store.UpdateMetadata(
            handle.Path,
            FromFileTime(creationTime),
            FromFileTime(lastAccessTime),
            FromFileTime(lastWriteTime));
        store.MarkDirty(handle.Path);
        FillInfo(store.Get(handle.Path)!, out fileInfo);
        return STATUS_SUCCESS;
    }

    public override int SetFileSize(
        object fileNode,
        object fileDesc,
        ulong newSize,
        bool setAllocationSize,
        out FileInfo fileInfo)
    {
        var handle = Handle(fileDesc);
        if (!EnsureWritable(handle, out _, out var status))
        {
            fileInfo = default;
            return status;
        }
        if (handle.Stream is null)
        {
            fileInfo = default;
            return STATUS_FILE_IS_A_DIRECTORY;
        }

        if (!setAllocationSize || (ulong)handle.Stream.Length > newSize)
            handle.Stream.SetLength(checked((long)newSize));
        handle.Modified = true;
        store.MarkDirty(handle.Path);
        FillInfo(store.Get(handle.Path)!, out fileInfo);
        return STATUS_SUCCESS;
    }

    public override int CanDelete(object fileNode, object fileDesc, string fileName)
    {
        var handle = Handle(fileDesc);
        var entry = store.Get(handle.Path);
        if (entry is null)
            return STATUS_OBJECT_NAME_NOT_FOUND;
        if (entry.Path.Length == 0)
            return STATUS_ACCESS_DENIED;
        if (entry.IsFolder && store.GetChildren(entry.Path).Count != 0)
            return STATUS_DIRECTORY_NOT_EMPTY;
        return STATUS_SUCCESS;
    }

    public override int Rename(
        object fileNode,
        object fileDesc,
        string fileName,
        string newFileName,
        bool replaceIfExists)
    {
        var handle = Handle(fileDesc);
        var entry = store.Get(handle.Path);
        if (entry is null)
            return STATUS_OBJECT_NAME_NOT_FOUND;
        if (entry.Path.Length == 0)
            return STATUS_ACCESS_DENIED;

        var newPath = ManifestStore.Normalize(newFileName);
        if (entry.IsRemote)
            api.RenameAsync(handle.Path, newPath, CancellationToken.None).GetAwaiter().GetResult();
        store.RenameLocal(handle.Path, newPath, replaceIfExists);
        if (entry.IsRemote)
            store.MarkRemoteMutation(newPath);
        handle.Path = newPath;
        return STATUS_SUCCESS;
    }

    public override bool ReadDirectoryEntry(
        object fileNode,
        object fileDesc,
        string pattern,
        string marker,
        ref object context,
        out string fileName,
        out FileInfo fileInfo)
    {
        var handle = Handle(fileDesc);
        if (!handle.IsFolder)
        {
            fileName = null!;
            fileInfo = default;
            return false;
        }

        var entries = handle.DirectoryEntries ??= BuildDirectoryEntries(handle.Path);
        var index = context is int value ? value : StartIndex(entries, marker);
        if (index >= entries.Count)
        {
            fileName = null!;
            fileInfo = default;
            return false;
        }

        context = index + 1;
        var item = entries[index];
        fileName = item.Name;
        FillInfo(item.Entry, out fileInfo);
        return true;
    }

    public override int GetDirInfoByName(
        object fileNode,
        object fileDesc,
        string fileName,
        out string normalizedName,
        out FileInfo fileInfo)
    {
        var handle = Handle(fileDesc);
        var child = store.Get(
            string.IsNullOrEmpty(handle.Path) ? fileName : $"{handle.Path}/{fileName}");
        if (child is null || child.State == EntryState.Deleted)
        {
            normalizedName = null!;
            fileInfo = default;
            return STATUS_OBJECT_NAME_NOT_FOUND;
        }

        normalizedName = Path.GetFileName(child.Path.Replace('/', '\\'));
        FillInfo(child, out fileInfo);
        return STATUS_SUCCESS;
    }

    public override int GetSecurity(object fileNode, object fileDesc, ref byte[] securityDescriptor)
    {
        securityDescriptor = SecurityDescriptor;
        return STATUS_SUCCESS;
    }

    private FileHandle OpenHandle(ManifestEntry entry, bool writable)
    {
        if (!entry.IsFolder)
        {
            var local = store.CachePath(entry.Path);
            if (!File.Exists(local))
            {
                if (!entry.IsRemote)
                    throw new FileNotFoundException("Локальный файл кэша не найден.", local);
                api.DownloadAsync(entry.Path, local, CancellationToken.None)
                    .GetAwaiter()
                    .GetResult();
            }
            store.TouchAccess(entry.Path);
        }

        return new FileHandle
        {
            Path = entry.Path,
            IsFolder = entry.IsFolder,
            Stream = entry.IsFolder
                ? null
                : new FileStream(
                    store.CachePath(entry.Path),
                    FileMode.Open,
                    writable ? FileAccess.ReadWrite : FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete,
                    128 * 1024,
                    FileOptions.RandomAccess)
        };
    }

    private FileStream OpenStream(string path, bool writable)
        => new(
            store.CachePath(path),
            FileMode.Open,
            writable ? FileAccess.ReadWrite : FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);

    private bool EnsureWritable(
        FileHandle handle,
        out ManifestEntry entry,
        out int status)
    {
        entry = store.Get(handle.Path)!;
        if (entry is null)
        {
            status = STATUS_OBJECT_NAME_NOT_FOUND;
            return false;
        }
        if (!IsWritable(entry))
        {
            status = STATUS_ACCESS_DENIED;
            return false;
        }
        status = STATUS_SUCCESS;
        return true;
    }

    private bool IsWritable(ManifestEntry entry)
        => entry.State == EntryState.New ||
           (entry.IsRemote && !entry.IsFolder && entry.CheckedOutById == currentUserId) ||
           entry.State is EntryState.Dirty or EntryState.Renamed;

    private uint Attributes(ManifestEntry entry)
    {
        var result = entry.IsFolder
            ? (uint)System.IO.FileAttributes.Directory
            : (uint)System.IO.FileAttributes.Archive;
        if (!entry.IsFolder && !IsWritable(entry))
            result |= (uint)System.IO.FileAttributes.ReadOnly;
        return result;
    }

    private void FillInfo(ManifestEntry entry, out FileInfo fileInfo)
    {
        var localPath = store.CachePath(entry.Path);
        var size = !entry.IsFolder && File.Exists(localPath)
            ? (ulong)new System.IO.FileInfo(localPath).Length
            : (ulong)Math.Max(0, entry.SizeBytes);
        fileInfo = default;
        fileInfo.FileAttributes = Attributes(entry);
        fileInfo.FileSize = size;
        fileInfo.AllocationSize = (size + AllocationUnit - 1) / AllocationUnit * AllocationUnit;
        fileInfo.CreationTime = (ulong)entry.CreatedAt.UtcDateTime.ToFileTimeUtc();
        var accessedAt = entry.AccessedAt == default ? entry.UpdatedAt : entry.AccessedAt;
        fileInfo.LastAccessTime = (ulong)accessedAt.UtcDateTime.ToFileTimeUtc();
        fileInfo.LastWriteTime = (ulong)entry.UpdatedAt.UtcDateTime.ToFileTimeUtc();
        fileInfo.ChangeTime = fileInfo.LastWriteTime;
        fileInfo.IndexNumber = StableIndex(entry.Path);
        fileInfo.HardLinks = 0;
    }

    private List<DirectoryItem> BuildDirectoryEntries(string path)
    {
        var result = new List<DirectoryItem>();
        var current = store.Get(path)!;
        if (path.Length != 0)
        {
            result.Add(new DirectoryItem(".", current));
            result.Add(new DirectoryItem("..", store.Get(ManifestStore.Parent(path))!));
        }
        result.AddRange(store.GetChildren(path).Select(x =>
            new DirectoryItem(Path.GetFileName(x.Path.Replace('/', '\\')), x)));
        return result.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static int StartIndex(IReadOnlyList<DirectoryItem> entries, string? marker)
    {
        if (string.IsNullOrEmpty(marker))
            return 0;
        for (var index = 0; index < entries.Count; index++)
        {
            if (string.Compare(entries[index].Name, marker, StringComparison.OrdinalIgnoreCase) > 0)
                return index;
        }
        return entries.Count;
    }

    private static FileHandle Handle(object value)
        => (FileHandle)value;

    private static DateTimeOffset? FromFileTime(ulong value)
        => value == 0 ? null : new DateTimeOffset(DateTime.FromFileTimeUtc((long)value));

    private static ulong StableIndex(string path)
    {
        const ulong offset = 14695981039346656037;
        const ulong prime = 1099511628211;
        var hash = offset;
        foreach (var value in path.ToUpperInvariant())
        {
            hash ^= value;
            hash *= prime;
        }
        return hash;
    }

    private static ulong GetCacheSize(string path)
    {
        try
        {
            return (ulong)Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories)
                .Sum(x => new System.IO.FileInfo(x).Length);
        }
        catch
        {
            return 0;
        }
    }

    private static byte[] CreateSecurityDescriptor()
    {
        var userSid = WindowsIdentity.GetCurrent().User
                      ?? throw new InvalidOperationException("The current user SID is unavailable.");
        var descriptor = new RawSecurityDescriptor(
            $"O:{userSid.Value}G:{userSid.Value}" +
            $"D:P(A;;FA;;;SY)(A;;FA;;;BA)(A;;FA;;;{userSid.Value})");
        var bytes = new byte[descriptor.BinaryLength];
        descriptor.GetBinaryForm(bytes, 0);
        return bytes;
    }

    private sealed class FileHandle
    {
        public required string Path { get; set; }
        public bool IsFolder { get; init; }
        public FileStream? Stream { get; set; }
        public bool Modified { get; set; }
        public bool Deleted { get; set; }
        public List<DirectoryItem>? DirectoryEntries { get; set; }
    }

    private sealed record DirectoryItem(string Name, ManifestEntry Entry);
}
