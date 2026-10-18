using Microsoft.EntityFrameworkCore;
using TNovCloud.Api.Data;
using TNovCloud.Shared;

namespace TNovCloud.Api.Services;

public sealed class CloudService(CloudDbContext db, StorageService storage)
{
    public async Task<User> RequireUserAsync(Guid userId, CancellationToken ct)
        => await db.Users.FirstOrDefaultAsync(x => x.Id == userId, ct)
           ?? throw new CloudException(401, "Пользователь не найден.");

    public async Task<LoginResponse> LoginAsync(LoginRequest request, JwtTokenService jwt, CancellationToken ct)
    {
        var login = request.Login.Trim();
        var user = await db.Users.FirstOrDefaultAsync(x => x.Login == login, ct)
                   ?? throw new CloudException(401, "Неверный логин или пароль.");
        if (!BCrypt.Net.BCrypt.Verify(request.Password, user.PasswordHash))
            throw new CloudException(401, "Неверный логин или пароль.");
        return new LoginResponse(jwt.Create(user), ToDto(user));
    }

    public async Task<IReadOnlyList<UserDto>> ListUsersAsync(CancellationToken ct)
        => await db.Users.OrderBy(x => x.DisplayName)
            .Select(x => new UserDto(x.Id, x.Login, x.DisplayName, x.Email))
            .ToListAsync(ct);

    public async Task<IReadOnlyList<FsEntryDto>> GetTreeAsync(CancellationToken ct)
    {
        var allFolders = await db.Folders.AsNoTracking().ToListAsync(ct);
        var allFolderMap = allFolders.ToDictionary(x => x.Id);
        var folders = allFolders.Where(x => IsVisible(x, allFolderMap)).ToList();
        var activeFolderIds = folders.Select(x => x.Id).ToHashSet();
        var files = await db.Files.AsNoTracking().Where(x => x.DeletedAt == null && activeFolderIds.Contains(x.FolderId))
            .Include(x => x.CheckedOutBy).Include(x => x.Versions).ToListAsync(ct);
        var folderMap = folders.ToDictionary(x => x.Id);
        var result = new List<FsEntryDto>();

        foreach (var folder in folders.Where(x => x.ParentId != null).OrderBy(x => x.Name))
            result.Add(ToEntry(folder, PathOf(folder, folderMap)));

        foreach (var file in files.OrderBy(x => x.Name))
        {
            folderMap.TryGetValue(file.FolderId, out var folder);
            var dir = folder is null ? "" : PathOf(folder, folderMap);
            var path = string.IsNullOrEmpty(dir) ? file.Name : $"{dir}/{file.Name}";
            result.Add(ToEntry(file, path));
        }

        return result;
    }

    private static bool IsVisible(Folder folder, IReadOnlyDictionary<Guid, Folder> map)
    {
        Folder? current = folder;
        while (current is not null)
        {
            if (current.DeletedAt is not null)
                return false;
            current = current.ParentId is { } parentId && map.TryGetValue(parentId, out var parent)
                ? parent
                : null;
        }
        return true;
    }

    public async Task EnsureFolderAsync(string path, Guid userId, CancellationToken ct)
    {
        await ResolveFolderAsync(path, userId, create: true, ct);
    }

    public async Task<StoredFile> UploadNewAsync(string path, Stream content, string? comment, Guid userId, CancellationToken ct)
    {
        var (dir, name) = Split(path);
        var folder = await ResolveFolderAsync(dir, userId, create: true, ct);
        if (await db.Folders.AnyAsync(
                x => x.ParentId == folder.Id && x.Name == name && x.DeletedAt == null, ct))
            throw new CloudException(409, "Папка с таким именем уже существует.");
        var existing = await db.Files.FirstOrDefaultAsync(
            x => x.FolderId == folder.Id && x.Name == name && x.DeletedAt == null, ct);
        if (existing is not null)
        {
            if (existing.CheckedOutById == userId)
                return await CheckinAsync(path, content, comment, userId, keepCheckout: false, existing.CurrentVersion, ct);
            throw new CloudException(409, "Файл уже существует. Возьмите его на редактирование.");
        }

        var now = DateTimeOffset.UtcNow;
        var file = new StoredFile
        {
            Id = Guid.NewGuid(),
            FolderId = folder.Id,
            Name = name,
            CurrentVersion = 1,
            Status = FileDocStatus.Draft,
            CreatedAt = now,
            UpdatedAt = now,
            Revision = 1
        };
        var (key, size) = await storage.SaveAsync(file.Id, 1, content, ct);
        file.Versions.Add(new FileVersion
        {
            Id = Guid.NewGuid(),
            FileId = file.Id,
            Version = 1,
            StorageKey = key,
            SizeBytes = size,
            Comment = comment ?? "Создание",
            CreatedById = userId,
            CreatedAt = now
        });
        db.Files.Add(file);
        AddAudit("created", file, path, null, userId, comment);
        await SaveChangesAsync(ct);
        return file;
    }

    public async Task CheckoutAsync(string path, Guid userId, CancellationToken ct)
    {
        var file = await FindFileAsync(path, ct);
        if (file.Status == FileDocStatus.InReview)
            throw new CloudException(409, "Файл на согласовании, редактирование запрещено.");
        if (file.CheckedOutById is { } other && other != userId)
        {
            var name = file.CheckedOutBy?.DisplayName ?? "другой пользователь";
            throw new CloudException(409, $"Файл уже взят на редактирование: {name}.");
        }

        file.CheckedOutById = userId;
        file.CheckedOutAt = DateTimeOffset.UtcNow;
        file.UpdatedAt = DateTimeOffset.UtcNow;
        file.Revision++;
        AddAudit("checked-out", file, path, null, userId);
        await SaveChangesAsync(ct);
    }

    public async Task<StoredFile> CheckinAsync(
        string path,
        Stream content,
        string? comment,
        Guid userId,
        bool keepCheckout,
        int expectedVersion,
        CancellationToken ct)
    {
        var file = await FindFileAsync(path, ct);
        if (file.CheckedOutById != userId)
            throw new CloudException(409, "Файл не взят вами на редактирование.");
        if (file.Status == FileDocStatus.InReview)
            throw new CloudException(409, "Файл на согласовании.");
        if (file.CurrentVersion != expectedVersion)
            throw new CloudException(409, $"Версия файла изменилась. Ожидалась {expectedVersion}, текущая {file.CurrentVersion}.");

        var next = file.CurrentVersion + 1;
        var (key, size) = await storage.SaveAsync(file.Id, next, content, ct);

        file.CurrentVersion = next;
        file.Status = FileDocStatus.Draft;
        file.UpdatedAt = DateTimeOffset.UtcNow;
        file.Revision++;
        if (!keepCheckout)
        {
            file.CheckedOutById = null;
            file.CheckedOutAt = null;
        }

        db.FileVersions.Add(new FileVersion
        {
            Id = Guid.NewGuid(),
            FileId = file.Id,
            Version = next,
            StorageKey = key,
            SizeBytes = size,
            Comment = comment,
            CreatedById = userId,
            CreatedAt = DateTimeOffset.UtcNow
        });
        AddAudit("checked-in", file, path, null, userId, comment);
        await SaveChangesAsync(ct);
        return file;
    }

    public async Task CancelCheckoutAsync(string path, Guid userId, CancellationToken ct)
    {
        var file = await FindFileAsync(path, ct);
        if (file.CheckedOutById != userId)
            throw new CloudException(409, "Файл не взят вами на редактирование.");
        file.CheckedOutById = null;
        file.CheckedOutAt = null;
        file.UpdatedAt = DateTimeOffset.UtcNow;
        file.Revision++;
        AddAudit("checkout-cancelled", file, path, null, userId);
        await SaveChangesAsync(ct);
    }

    public async Task<(StoredFile File, FileVersion Version)> OpenContentAsync(string path, int? version, CancellationToken ct)
    {
        var file = await FindFileAsync(path, ct);
        var v = version ?? file.CurrentVersion;
        var row = await db.FileVersions.FirstOrDefaultAsync(x => x.FileId == file.Id && x.Version == v, ct)
                  ?? throw new CloudException(404, $"Версия {v} не найдена.");
        return (file, row);
    }

    public async Task RestoreAsync(string path, int version, Guid userId, CancellationToken ct)
    {
        var file = await FindFileAsync(path, ct);
        if (file.CheckedOutById is not null)
            throw new CloudException(409, "Сначала верните файл из редактирования.");
        if (file.Status == FileDocStatus.InReview)
            throw new CloudException(409, "Файл на согласовании.");

        var source = await db.FileVersions.FirstOrDefaultAsync(x => x.FileId == file.Id && x.Version == version, ct)
                     ?? throw new CloudException(404, $"Версия {version} не найдена.");
        var next = file.CurrentVersion + 1;
        var (key, size) = await storage.CopyVersionAsync(source.StorageKey, file.Id, next, ct);
        file.CurrentVersion = next;
        file.Status = FileDocStatus.Draft;
        file.UpdatedAt = DateTimeOffset.UtcNow;
        file.Revision++;
        db.FileVersions.Add(new FileVersion
        {
            Id = Guid.NewGuid(),
            FileId = file.Id,
            Version = next,
            StorageKey = key,
            SizeBytes = size,
            Comment = $"Восстановлено из версии {version}",
            CreatedById = userId,
            CreatedAt = DateTimeOffset.UtcNow
        });
        AddAudit("version-restored", file, path, null, userId, $"Source version: {version}");
        await SaveChangesAsync(ct);
    }

    public async Task<FileDetailsDto> GetDetailsAsync(string path, CancellationToken ct)
    {
        var file = await FindFileAsync(path, ct);
        var versions = await db.FileVersions.AsNoTracking()
            .Include(x => x.CreatedBy)
            .Where(x => x.FileId == file.Id)
            .OrderByDescending(x => x.Version)
            .ToListAsync(ct);
        var folders = await db.Folders.AsNoTracking().ToListAsync(ct);
        var folderMap = folders.ToDictionary(x => x.Id);
        var dir = folderMap.TryGetValue(file.FolderId, out var folder) ? PathOf(folder, folderMap) : "";
        var filePath = string.IsNullOrEmpty(dir) ? file.Name : $"{dir}/{file.Name}";
        return new FileDetailsDto(
            ToEntry(file, filePath),
            versions.Select(x => new FileVersionDto(x.Version, x.SizeBytes, x.Comment, x.CreatedBy.DisplayName, x.CreatedAt)).ToList());
    }

    public async Task<DirectoryChildrenDto> GetChildrenAsync(
        string path,
        bool includeDeleted,
        string? cursor,
        int pageSize,
        CancellationToken ct)
    {
        pageSize = Math.Clamp(pageSize, 1, 500);
        var offset = DecodeCursor(cursor);
        var folder = await ResolveFolderAsync(path, Guid.Empty, create: false, ct);
        var folders = await db.Folders.AsNoTracking()
            .Where(x => x.ParentId == folder.Id && (includeDeleted || x.DeletedAt == null))
            .ToListAsync(ct);
        var files = await db.Files.AsNoTracking()
            .Include(x => x.CheckedOutBy)
            .Include(x => x.Versions)
            .Where(x => x.FolderId == folder.Id && (includeDeleted || x.DeletedAt == null))
            .ToListAsync(ct);
        var directoryPath = NormalizePath(path);
        var allChildren = folders
            .Select(x => ToMetadata(x, Combine(directoryPath, x.Name)))
            .Concat(files.Select(x => ToMetadata(x, Combine(directoryPath, x.Name))))
            .OrderByDescending(x => x.IsFolder)
            .ThenBy(x => x.Name)
            .ToList();
        var children = allChildren.Skip(offset).Take(pageSize).ToList();
        var nextCursor = offset + children.Count < allChildren.Count
            ? EncodeCursor(offset + children.Count)
            : null;
        return new DirectoryChildrenDto(
            ToMetadata(folder, directoryPath),
            children,
            nextCursor);
    }

    public async Task<FsMetadataDto> GetMetadataAsync(string path, bool includeDeleted, CancellationToken ct)
    {
        var entry = await ResolveEntryAsync(path, includeDeleted, ct);
        return entry.Folder is not null
            ? ToMetadata(entry.Folder, entry.Path)
            : ToMetadata(entry.File!, entry.Path);
    }

    public async Task<FsMetadataDto> RenameAsync(
        RenameEntryRequest request,
        Guid userId,
        CancellationToken ct)
    {
        var newName = ValidateName(request.NewName);
        var entry = await ResolveEntryAsync(request.Path, includeDeleted: false, ct);
        EnsureExpectedRevision(entry, request.ExpectedRevision);
        await EnsureNotInReviewAsync(entry, ct);
        var previousPath = entry.Path;

        if (entry.Folder is { } folder)
        {
            if (folder.ParentId is null)
                throw new CloudException(400, "Корневую папку нельзя переименовать.");
            await EnsureFolderNameAvailableAsync(folder.ParentId.Value, newName, folder.Id, ct);
            folder.Name = newName;
            Touch(folder);
            entry.Path = Combine(ParentPath(previousPath), newName);
            AddAudit("renamed", folder, entry.Path, previousPath, userId);
        }
        else
        {
            var file = entry.File!;
            await EnsureFileNameAvailableAsync(file.FolderId, newName, file.Id, ct);
            file.Name = newName;
            Touch(file);
            entry.Path = Combine(ParentPath(previousPath), newName);
            AddAudit("renamed", file, entry.Path, previousPath, userId);
        }

        await SaveChangesAsync(ct);
        return entry.Folder is not null
            ? ToMetadata(entry.Folder, entry.Path)
            : ToMetadata(entry.File!, entry.Path);
    }

    public async Task<FsMetadataDto> MoveAsync(
        MoveEntryRequest request,
        Guid userId,
        CancellationToken ct)
    {
        var entry = await ResolveEntryAsync(request.Path, includeDeleted: false, ct);
        EnsureExpectedRevision(entry, request.ExpectedRevision);
        await EnsureNotInReviewAsync(entry, ct);
        var destination = await ResolveFolderAsync(request.DestinationDirectory, userId, create: false, ct);
        var previousPath = entry.Path;
        var destinationPath = NormalizePath(request.DestinationDirectory);

        if (entry.Folder is { } folder)
        {
            if (folder.ParentId is null)
                throw new CloudException(400, "Корневую папку нельзя перемещать.");
            if (folder.Id == destination.Id || await IsDescendantAsync(destination.Id, folder.Id, ct))
                throw new CloudException(400, "Папку нельзя переместить внутрь самой себя.");
            await EnsureFolderNameAvailableAsync(destination.Id, folder.Name, folder.Id, ct);
            folder.ParentId = destination.Id;
            Touch(folder);
            entry.Path = Combine(destinationPath, folder.Name);
            AddAudit("moved", folder, entry.Path, previousPath, userId);
        }
        else
        {
            var file = entry.File!;
            await EnsureFileNameAvailableAsync(destination.Id, file.Name, file.Id, ct);
            file.FolderId = destination.Id;
            Touch(file);
            entry.Path = Combine(destinationPath, file.Name);
            AddAudit("moved", file, entry.Path, previousPath, userId);
        }

        await SaveChangesAsync(ct);
        return entry.Folder is not null
            ? ToMetadata(entry.Folder, entry.Path)
            : ToMetadata(entry.File!, entry.Path);
    }

    public async Task SoftDeleteAsync(DeleteEntryRequest request, Guid userId, CancellationToken ct)
    {
        var entry = await ResolveEntryAsync(request.Path, includeDeleted: false, ct);
        EnsureExpectedRevision(entry, request.ExpectedRevision);
        await EnsureNotInReviewAsync(entry, ct);
        var now = DateTimeOffset.UtcNow;

        if (entry.Folder is { } folder)
        {
            if (folder.ParentId is null)
                throw new CloudException(400, "Корневую папку нельзя удалить.");
            folder.DeletedAt = now;
            folder.DeletedById = userId;
            Touch(folder, now);
            AddAudit("deleted", folder, entry.Path, null, userId);
        }
        else
        {
            var file = entry.File!;
            if (file.Status == FileDocStatus.InReview)
                throw new CloudException(409, "Файл на согласовании.");
            if (file.CheckedOutById is not null)
                throw new CloudException(409, "Сначала верните файл из редактирования.");
            file.DeletedAt = now;
            file.DeletedById = userId;
            Touch(file, now);
            AddAudit("deleted", file, entry.Path, null, userId);
        }

        await SaveChangesAsync(ct);
    }

    public async Task<FsMetadataDto> RestoreDeletedAsync(
        RestoreDeletedEntryRequest request,
        Guid userId,
        CancellationToken ct)
    {
        var entry = await ResolveDeletedEntryAsync(request.Path, ct);
        EnsureExpectedRevision(entry, request.ExpectedRevision);
        if (entry.Folder?.DeletedAt is null && entry.File?.DeletedAt is null)
            throw new CloudException(409, "Объект не удалён.");

        if (entry.Folder is { } folder)
        {
            await EnsureFolderNameAvailableAsync(folder.ParentId!.Value, folder.Name, folder.Id, ct);
            folder.DeletedAt = null;
            folder.DeletedById = null;
            Touch(folder);
            AddAudit("restored", folder, entry.Path, null, userId);
        }
        else
        {
            var file = entry.File!;
            await EnsureFileNameAvailableAsync(file.FolderId, file.Name, file.Id, ct);
            file.DeletedAt = null;
            file.DeletedById = null;
            Touch(file);
            AddAudit("restored", file, entry.Path, null, userId);
        }

        await SaveChangesAsync(ct);
        return entry.Folder is not null
            ? ToMetadata(entry.Folder, entry.Path)
            : ToMetadata(entry.File!, entry.Path);
    }

    public async Task<IReadOnlyList<AuditEventDto>> GetAuditAsync(
        string? path,
        int limit,
        CancellationToken ct)
    {
        var query = db.FileAuditEvents.AsNoTracking().Include(x => x.Actor).AsQueryable();
        if (!string.IsNullOrWhiteSpace(path))
        {
            var entry = await ResolveEntryAsync(path, includeDeleted: true, ct);
            query = query.Where(x => x.EntryId == entry.Id);
        }

        return await query.OrderByDescending(x => x.CreatedAt)
            .Take(Math.Clamp(limit, 1, 500))
            .Select(x => new AuditEventDto(
                x.Id, x.Action, x.IsFolder, x.EntryId, x.Path, x.PreviousPath,
                x.Version, x.Revision, x.ActorId, x.Actor.DisplayName, x.CreatedAt, x.Details))
            .ToListAsync(ct);
    }

    public async Task<ProcessDto> StartProcessAsync(CreateProcessRequest request, Guid userId, CancellationToken ct)
    {
        if (request.AssigneeIds.Count == 0)
            throw new CloudException(400, "Укажите хотя бы одного согласующего.");

        var file = await FindFileAsync(request.Path, ct);
        if (file.CheckedOutById is not null)
            throw new CloudException(409, "Верните файл из редактирования перед согласованием.");
        if (file.Status == FileDocStatus.InReview)
            throw new CloudException(409, "По файлу уже идёт согласование.");

        var assignees = await db.Users.Where(x => request.AssigneeIds.Contains(x.Id)).ToListAsync(ct);
        if (assignees.Count != request.AssigneeIds.Count)
            throw new CloudException(400, "Один из согласующих не найден.");

        var process = new ApprovalProcess
        {
            Id = Guid.NewGuid(),
            FileId = file.Id,
            Title = string.IsNullOrWhiteSpace(request.Title) ? $"Согласование {file.Name}" : request.Title.Trim(),
            Status = ProcessStatus.Running,
            InitiatorId = userId,
            CreatedAt = DateTimeOffset.UtcNow
        };
        var order = 1;
        foreach (var id in request.AssigneeIds)
        {
            process.Tasks.Add(new ApprovalTask
            {
                Id = Guid.NewGuid(),
                ProcessId = process.Id,
                StepOrder = order,
                AssigneeId = id,
                Status = order == 1 ? ApprovalTaskStatus.Pending : ApprovalTaskStatus.Waiting,
                DueAt = request.DueAt
            });
            order++;
        }

        file.Status = FileDocStatus.InReview;
        Touch(file);
        db.Processes.Add(process);
        AddAudit("review-started", file, NormalizePath(request.Path), null, userId, process.Title);
        await SaveChangesAsync(ct);
        return await GetProcessAsync(process.Id, ct);
    }

    public async Task<ProcessDto> DecideAsync(Guid taskId, DecideRequest request, Guid userId, CancellationToken ct)
    {
        var task = await db.Tasks.Include(x => x.Process).ThenInclude(x => x.Tasks)
            .Include(x => x.Process).ThenInclude(x => x.File)
            .FirstOrDefaultAsync(x => x.Id == taskId, ct)
            ?? throw new CloudException(404, "Задача не найдена.");
        if (task.AssigneeId != userId)
            throw new CloudException(403, "Это не ваша задача.");
        if (task.Status != ApprovalTaskStatus.Pending || task.Process.Status != ProcessStatus.Running)
            throw new CloudException(409, "Задачу нельзя выполнить.");

        var action = request.Action.Trim().ToLowerInvariant();
        task.Comment = request.Comment;
        task.CompletedAt = DateTimeOffset.UtcNow;

        if (action is "reject" or "отклонено")
        {
            task.Status = ApprovalTaskStatus.Rejected;
            task.Process.Status = ProcessStatus.Rejected;
            task.Process.CompletedAt = DateTimeOffset.UtcNow;
            task.Process.File.Status = FileDocStatus.Rejected;
        }
        else if (action is "approve" or "согласовано")
        {
            task.Status = ApprovalTaskStatus.Approved;
            var next = task.Process.Tasks.OrderBy(x => x.StepOrder).FirstOrDefault(x => x.StepOrder > task.StepOrder);
            if (next is null)
            {
                task.Process.Status = ProcessStatus.Completed;
                task.Process.CompletedAt = DateTimeOffset.UtcNow;
                task.Process.File.Status = FileDocStatus.Approved;
            }
            else
            {
                next.Status = ApprovalTaskStatus.Pending;
            }
        }
        else
        {
            throw new CloudException(400, "Действие: approve или reject.");
        }

        Touch(task.Process.File);
        var filePath = await PathForFileAsync(task.Process.File, ct);
        AddAudit("review-decided", task.Process.File, filePath, null, userId, action);
        await SaveChangesAsync(ct);
        return await GetProcessAsync(task.ProcessId, ct);
    }

    public async Task<IReadOnlyList<ProcessTaskDto>> InboxAsync(Guid userId, CancellationToken ct)
    {
        var tasks = await db.Tasks.AsNoTracking()
            .Include(x => x.Assignee)
            .Include(x => x.Process).ThenInclude(x => x.File).ThenInclude(x => x.Folder)
            .Where(x => x.AssigneeId == userId && x.Status == ApprovalTaskStatus.Pending)
            .OrderBy(x => x.DueAt)
            .ToListAsync(ct);
        var folders = await db.Folders.AsNoTracking().ToListAsync(ct);
        var map = folders.ToDictionary(x => x.Id);
        return tasks.Select(t => ToTaskDto(t, map)).ToList();
    }

    public async Task<ProcessDto> GetProcessAsync(Guid id, CancellationToken ct)
    {
        var process = await db.Processes.AsNoTracking()
            .Include(x => x.Initiator)
            .Include(x => x.File)
            .Include(x => x.Tasks).ThenInclude(x => x.Assignee)
            .FirstOrDefaultAsync(x => x.Id == id, ct)
            ?? throw new CloudException(404, "Процесс не найден.");
        var folders = await db.Folders.AsNoTracking().ToListAsync(ct);
        var map = folders.ToDictionary(x => x.Id);
        var dir = map.TryGetValue(process.File.FolderId, out var folder) ? PathOf(folder, map) : "";
        var filePath = string.IsNullOrEmpty(dir) ? process.File.Name : $"{dir}/{process.File.Name}";
        return new ProcessDto(
            process.Id,
            process.Title,
            process.Status,
            filePath,
            process.Initiator.DisplayName,
            process.CreatedAt,
            process.Tasks.OrderBy(x => x.StepOrder).Select(t => ToTaskDto(t, map, filePath)).ToList());
    }

    public Stream OpenStorage(string storageKey) => storage.Open(storageKey);

    private async Task<StoredFile> FindFileAsync(string path, CancellationToken ct)
    {
        var (dir, name) = Split(path);
        var folder = await ResolveFolderAsync(dir, Guid.Empty, create: false, ct);
        return await db.Files.Include(x => x.CheckedOutBy).Include(x => x.Versions)
                   .FirstOrDefaultAsync(x => x.FolderId == folder.Id && x.Name == name && x.DeletedAt == null, ct)
               ?? throw new CloudException(404, "Файл не найден.");
    }

    private async Task<Folder> ResolveFolderAsync(string path, Guid userId, bool create, CancellationToken ct)
    {
        var root = await db.Folders.FirstOrDefaultAsync(x => x.ParentId == null && x.DeletedAt == null, ct)
                   ?? throw new CloudException(500, "Корневая папка не создана.");
        if (string.IsNullOrWhiteSpace(path))
            return root;

        var current = root;
        var currentPath = "";
        foreach (var segment in SplitSegments(path))
        {
            var next = await db.Folders.FirstOrDefaultAsync(
                x => x.ParentId == current.Id && x.Name == segment && x.DeletedAt == null, ct);
            if (next is null)
            {
                if (!create)
                    throw new CloudException(404, $"Папка не найдена: {path}");
                if (await db.Files.AnyAsync(
                        x => x.FolderId == current.Id && x.Name == segment && x.DeletedAt == null, ct))
                    throw new CloudException(409, $"Файл уже занимает путь: {Combine(currentPath, segment)}");
                var now = DateTimeOffset.UtcNow;
                next = new Folder
                {
                    Id = Guid.NewGuid(),
                    ParentId = current.Id,
                    Name = segment,
                    CreatedById = userId,
                    CreatedAt = now,
                    UpdatedAt = now,
                    Revision = 1
                };
                db.Folders.Add(next);
                AddAudit("created", next, Combine(currentPath, segment), null, userId);
                await SaveChangesAsync(ct);
            }

            current = next;
            currentPath = Combine(currentPath, segment);
        }

        return current;
    }

    private async Task<string> PathForFileAsync(StoredFile file, CancellationToken ct)
    {
        var folders = await db.Folders.AsNoTracking().ToListAsync(ct);
        var map = folders.ToDictionary(x => x.Id);
        var directory = map.TryGetValue(file.FolderId, out var folder) ? PathOf(folder, map) : "";
        return Combine(directory, file.Name);
    }

    private async Task<EntryResolution> ResolveEntryAsync(
        string path,
        bool includeDeleted,
        CancellationToken ct)
    {
        var normalized = NormalizePath(path);
        if (string.IsNullOrEmpty(normalized))
        {
            var root = await db.Folders.FirstOrDefaultAsync(x => x.ParentId == null, ct)
                       ?? throw new CloudException(404, "Корневая папка не найдена.");
            return new EntryResolution(root.Id, normalized, root, null);
        }

        var (dir, name) = Split(normalized);
        var parent = await ResolveFolderAsync(dir, Guid.Empty, create: false, ct);
        var folder = await db.Folders.FirstOrDefaultAsync(
            x => x.ParentId == parent.Id && x.Name == name && (includeDeleted || x.DeletedAt == null), ct);
        if (folder is not null)
            return new EntryResolution(folder.Id, normalized, folder, null);

        var file = await db.Files.Include(x => x.CheckedOutBy).Include(x => x.Versions)
            .FirstOrDefaultAsync(
                x => x.FolderId == parent.Id && x.Name == name && (includeDeleted || x.DeletedAt == null), ct);
        return file is not null
            ? new EntryResolution(file.Id, normalized, null, file)
            : throw new CloudException(404, "Файл или папка не найдены.");
    }

    private async Task<EntryResolution> ResolveDeletedEntryAsync(string path, CancellationToken ct)
    {
        var normalized = NormalizePath(path);
        if (string.IsNullOrEmpty(normalized))
            throw new CloudException(400, "Корневую папку нельзя восстановить.");

        var (directory, name) = Split(normalized);
        var parent = await ResolveFolderAsync(directory, Guid.Empty, create: false, ct);
        var folder = await db.Folders.FirstOrDefaultAsync(
            x => x.ParentId == parent.Id && x.Name == name && x.DeletedAt != null,
            ct);
        if (folder is not null)
            return new EntryResolution(folder.Id, normalized, folder, null);

        var file = await db.Files.Include(x => x.CheckedOutBy).Include(x => x.Versions)
            .FirstOrDefaultAsync(
                x => x.FolderId == parent.Id && x.Name == name && x.DeletedAt != null,
                ct);
        return file is not null
            ? new EntryResolution(file.Id, normalized, null, file)
            : throw new CloudException(404, "Удалённый файл или папка не найдены.");
    }

    private static void EnsureExpectedRevision(EntryResolution entry, long expectedRevision)
    {
        var actual = entry.Folder?.Revision ?? entry.File!.Revision;
        if (actual != expectedRevision)
            throw new CloudException(409, $"Объект изменился. Ожидалась ревизия {expectedRevision}, текущая {actual}.");
    }

    private async Task EnsureNotInReviewAsync(EntryResolution entry, CancellationToken ct)
    {
        if (entry.File is { Status: FileDocStatus.InReview })
            throw new CloudException(409, "Файл на согласовании; изменение пути или удаление запрещено.");
        if (entry.Folder is null)
            return;

        var folders = await db.Folders.AsNoTracking()
            .Where(x => x.DeletedAt == null)
            .Select(x => new { x.Id, x.ParentId })
            .ToListAsync(ct);
        var descendants = new HashSet<Guid> { entry.Folder.Id };
        var changed = true;
        while (changed)
        {
            changed = false;
            foreach (var folder in folders)
            {
                if (folder.ParentId is { } parent &&
                    descendants.Contains(parent) &&
                    descendants.Add(folder.Id))
                    changed = true;
            }
        }

        if (await db.Files.AnyAsync(
                x => descendants.Contains(x.FolderId) &&
                     x.DeletedAt == null &&
                     x.Status == FileDocStatus.InReview,
                ct))
            throw new CloudException(409, "В папке есть файл на согласовании; изменение пути или удаление запрещено.");
    }

    private async Task EnsureFolderNameAvailableAsync(
        Guid parentId,
        string name,
        Guid exceptId,
        CancellationToken ct)
    {
        if (await db.Folders.AnyAsync(
                x => x.ParentId == parentId && x.Name == name && x.DeletedAt == null && x.Id != exceptId, ct))
            throw new CloudException(409, "Папка с таким именем уже существует.");
        if (await db.Files.AnyAsync(
                x => x.FolderId == parentId && x.Name == name && x.DeletedAt == null, ct))
            throw new CloudException(409, "Файл с таким именем уже существует.");
    }

    private async Task EnsureFileNameAvailableAsync(
        Guid folderId,
        string name,
        Guid exceptId,
        CancellationToken ct)
    {
        if (await db.Files.AnyAsync(
                x => x.FolderId == folderId && x.Name == name && x.DeletedAt == null && x.Id != exceptId, ct))
            throw new CloudException(409, "Файл с таким именем уже существует.");
        if (await db.Folders.AnyAsync(
                x => x.ParentId == folderId && x.Name == name && x.DeletedAt == null, ct))
            throw new CloudException(409, "Папка с таким именем уже существует.");
    }

    private async Task<bool> IsDescendantAsync(Guid candidateId, Guid ancestorId, CancellationToken ct)
    {
        var folders = await db.Folders.AsNoTracking()
            .Select(x => new { x.Id, x.ParentId })
            .ToDictionaryAsync(x => x.Id, x => x.ParentId, ct);
        var current = candidateId;
        while (folders.TryGetValue(current, out var parentId) && parentId is { } parent)
        {
            if (parent == ancestorId)
                return true;
            current = parent;
        }
        return false;
    }

    private void AddAudit(
        string action,
        StoredFile file,
        string path,
        string? previousPath,
        Guid actorId,
        string? details = null)
        => db.FileAuditEvents.Add(new FileAuditEvent
        {
            Id = Guid.NewGuid(),
            Action = action,
            IsFolder = false,
            EntryId = file.Id,
            Path = path,
            PreviousPath = previousPath,
            Version = file.CurrentVersion,
            Revision = file.Revision,
            ActorId = actorId,
            CreatedAt = DateTimeOffset.UtcNow,
            Details = details
        });

    private void AddAudit(
        string action,
        Folder folder,
        string path,
        string? previousPath,
        Guid actorId,
        string? details = null)
        => db.FileAuditEvents.Add(new FileAuditEvent
        {
            Id = Guid.NewGuid(),
            Action = action,
            IsFolder = true,
            EntryId = folder.Id,
            Path = path,
            PreviousPath = previousPath,
            Revision = folder.Revision,
            ActorId = actorId,
            CreatedAt = DateTimeOffset.UtcNow,
            Details = details
        });

    private async Task SaveChangesAsync(CancellationToken ct)
    {
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            throw new CloudException(409, "Объект был изменён другим пользователем. Обновите данные и повторите операцию.");
        }
    }

    private static void Touch(StoredFile file, DateTimeOffset? now = null)
    {
        file.UpdatedAt = now ?? DateTimeOffset.UtcNow;
        file.Revision++;
    }

    private static void Touch(Folder folder, DateTimeOffset? now = null)
    {
        folder.UpdatedAt = now ?? DateTimeOffset.UtcNow;
        folder.Revision++;
    }

    private static string ValidateName(string value)
    {
        var name = value.Trim();
        if (string.IsNullOrEmpty(name) || name is "." or ".." ||
            name.Contains('/') || name.Contains('\\') ||
            name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new CloudException(400, "Некорректное имя.");
        return name;
    }

    private static string NormalizePath(string path)
        => string.Join('/', SplitSegments(path));

    private static int DecodeCursor(string? cursor)
    {
        if (string.IsNullOrWhiteSpace(cursor))
            return 0;
        if (!int.TryParse(cursor, out var offset) || offset < 0)
            throw new CloudException(400, "Некорректный cursor каталога.");
        return offset;
    }

    private static string EncodeCursor(int offset) => offset.ToString();

    private static string Combine(string directory, string name)
        => string.IsNullOrEmpty(directory) ? name : $"{directory}/{name}";

    private static string ParentPath(string path)
    {
        var index = path.LastIndexOf('/');
        return index < 0 ? "" : path[..index];
    }

    private static string PathOf(Folder folder, IReadOnlyDictionary<Guid, Folder> map)
    {
        var parts = new Stack<string>();
        var current = folder;
        while (current.ParentId is { } parentId && map.TryGetValue(parentId, out var parent))
        {
            parts.Push(current.Name);
            current = parent;
        }

        if (current.ParentId is null && !string.IsNullOrEmpty(current.Name))
            parts.Push(current.Name);
        else if (current.ParentId is not null)
            parts.Push(current.Name);

        return string.Join('/', parts);
    }

    private static (string Dir, string Name) Split(string path)
    {
        var parts = SplitSegments(path);
        if (parts.Count == 0)
            throw new CloudException(400, "Не указан путь к файлу.");
        var name = parts[^1];
        parts.RemoveAt(parts.Count - 1);
        return (string.Join('/', parts), name);
    }

    private static List<string> SplitSegments(string path)
    {
        var parts = path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Any(p => p is "." or ".." || p.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0))
            throw new CloudException(400, "Некорректный путь.");
        return parts.ToList();
    }

    private static UserDto ToDto(User user) => new(user.Id, user.Login, user.DisplayName, user.Email);

    private static FsMetadataDto ToMetadata(Folder folder, string path)
        => new(
            true,
            folder.Id,
            folder.ParentId,
            folder.Name,
            path,
            null,
            null,
            null,
            null,
            null,
            folder.CreatedAt,
            folder.UpdatedAt,
            folder.Revision,
            folder.DeletedAt is not null,
            folder.DeletedAt,
            folder.DeletedById);

    private static FsMetadataDto ToMetadata(StoredFile file, string path)
    {
        var size = file.Versions.FirstOrDefault(x => x.Version == file.CurrentVersion)?.SizeBytes
                   ?? file.Versions.MaxBy(x => x.Version)?.SizeBytes;
        return new FsMetadataDto(
            false,
            file.Id,
            file.FolderId,
            file.Name,
            path,
            file.CurrentVersion,
            file.Status,
            file.CheckedOutById,
            file.CheckedOutBy?.DisplayName,
            size,
            file.CreatedAt,
            file.UpdatedAt,
            file.Revision,
            file.DeletedAt is not null,
            file.DeletedAt,
            file.DeletedById);
    }

    private static FsEntryDto ToEntry(Folder folder, string path)
        => new(
            true,
            folder.Id,
            folder.ParentId,
            folder.Name,
            path,
            null,
            null,
            null,
            null,
            null,
            folder.UpdatedAt,
            folder.Revision);

    private static FsEntryDto ToEntry(StoredFile file, string path)
    {
        var size = file.Versions.FirstOrDefault(x => x.Version == file.CurrentVersion)?.SizeBytes
                   ?? file.Versions.MaxBy(x => x.Version)?.SizeBytes;
        return new FsEntryDto(
            false,
            file.Id,
            file.FolderId,
            file.Name,
            path,
            file.CurrentVersion,
            file.Status,
            file.CheckedOutById,
            file.CheckedOutBy?.DisplayName,
            size,
            file.UpdatedAt,
            file.Revision);
    }

    private static ProcessTaskDto ToTaskDto(ApprovalTask task, IReadOnlyDictionary<Guid, Folder> map, string? filePath = null)
    {
        var file = task.Process.File;
        if (filePath is null)
        {
            var dir = map.TryGetValue(file.FolderId, out var folder) ? PathOf(folder, map) : "";
            filePath = string.IsNullOrEmpty(dir) ? file.Name : $"{dir}/{file.Name}";
        }

        return new ProcessTaskDto(
            task.Id,
            task.ProcessId,
            task.StepOrder,
            task.AssigneeId,
            task.Assignee.DisplayName,
            task.Status,
            task.Comment,
            task.DueAt,
            filePath,
            file.Name,
            task.Process.Title);
    }

    private sealed class EntryResolution(
        Guid id,
        string path,
        Folder? folder,
        StoredFile? file)
    {
        public Guid Id { get; } = id;
        public string Path { get; set; } = path;
        public Folder? Folder { get; } = folder;
        public StoredFile? File { get; } = file;
    }
}

public sealed class CloudException(int statusCode, string message) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
}
