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
        var folders = await db.Folders.AsNoTracking().ToListAsync(ct);
        var files = await db.Files.AsNoTracking().Include(x => x.CheckedOutBy).Include(x => x.Versions).ToListAsync(ct);
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

    public async Task EnsureFolderAsync(string path, Guid userId, CancellationToken ct)
    {
        await ResolveFolderAsync(path, userId, create: true, ct);
    }

    public async Task<StoredFile> UploadNewAsync(string path, Stream content, string? comment, Guid userId, CancellationToken ct)
    {
        var (dir, name) = Split(path);
        var folder = await ResolveFolderAsync(dir, userId, create: true, ct);
        var existing = await db.Files.FirstOrDefaultAsync(x => x.FolderId == folder.Id && x.Name == name, ct);
        if (existing is not null)
        {
            if (existing.CheckedOutById == userId)
                return await CheckinAsync(path, content, comment, userId, keepCheckout: false, ct);
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
            UpdatedAt = now
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
        await db.SaveChangesAsync(ct);
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
        await db.SaveChangesAsync(ct);
    }

    public async Task<StoredFile> CheckinAsync(string path, Stream content, string? comment, Guid userId, bool keepCheckout, CancellationToken ct)
    {
        var file = await FindFileAsync(path, ct);
        if (file.CheckedOutById != userId)
            throw new CloudException(409, "Файл не взят вами на редактирование.");
        if (file.Status == FileDocStatus.InReview)
            throw new CloudException(409, "Файл на согласовании.");

        var next = file.CurrentVersion + 1;
        var (key, size) = await storage.SaveAsync(file.Id, next, content, ct);

        file.CurrentVersion = next;
        file.Status = FileDocStatus.Draft;
        file.UpdatedAt = DateTimeOffset.UtcNow;
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
        await db.SaveChangesAsync(ct);
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
        await db.SaveChangesAsync(ct);
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
        await db.SaveChangesAsync(ct);
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
        file.UpdatedAt = DateTimeOffset.UtcNow;
        db.Processes.Add(process);
        await db.SaveChangesAsync(ct);
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

        task.Process.File.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
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
                   .FirstOrDefaultAsync(x => x.FolderId == folder.Id && x.Name == name, ct)
               ?? throw new CloudException(404, "Файл не найден.");
    }

    private async Task<Folder> ResolveFolderAsync(string path, Guid userId, bool create, CancellationToken ct)
    {
        var root = await db.Folders.FirstOrDefaultAsync(x => x.ParentId == null, ct)
                   ?? throw new CloudException(500, "Корневая папка не создана.");
        if (string.IsNullOrWhiteSpace(path))
            return root;

        var current = root;
        foreach (var segment in SplitSegments(path))
        {
            var next = await db.Folders.FirstOrDefaultAsync(x => x.ParentId == current.Id && x.Name == segment, ct);
            if (next is null)
            {
                if (!create)
                    throw new CloudException(404, $"Папка не найдена: {path}");
                next = new Folder
                {
                    Id = Guid.NewGuid(),
                    ParentId = current.Id,
                    Name = segment,
                    CreatedById = userId,
                    CreatedAt = DateTimeOffset.UtcNow
                };
                db.Folders.Add(next);
                await db.SaveChangesAsync(ct);
            }

            current = next;
        }

        return current;
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

    private static FsEntryDto ToEntry(Folder folder, string path)
        => new(true, folder.Id, folder.ParentId, folder.Name, path, null, null, null, null, null, folder.CreatedAt);

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
            file.UpdatedAt);
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
}

public sealed class CloudException(int statusCode, string message) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
}
