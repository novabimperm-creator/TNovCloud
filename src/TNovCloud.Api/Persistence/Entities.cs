using TNovCloud.Shared;

namespace TNovCloud.Api.Data;

public sealed class User
{
    public Guid Id { get; set; }
    public string Login { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string? Email { get; set; }
    public string PasswordHash { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class Folder
{
    public Guid Id { get; set; }
    public Guid? ParentId { get; set; }
    public Folder? Parent { get; set; }
    public string Name { get; set; } = "";
    public Guid CreatedById { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public long Revision { get; set; } = 1;
    public DateTimeOffset? DeletedAt { get; set; }
    public Guid? DeletedById { get; set; }
    public ICollection<Folder> Children { get; set; } = new List<Folder>();
    public ICollection<StoredFile> Files { get; set; } = new List<StoredFile>();
}

public sealed class StoredFile
{
    public Guid Id { get; set; }
    public Guid FolderId { get; set; }
    public Folder Folder { get; set; } = null!;
    public string Name { get; set; } = "";
    public int CurrentVersion { get; set; } = 1;
    public FileDocStatus Status { get; set; } = FileDocStatus.Draft;
    public Guid? CheckedOutById { get; set; }
    public User? CheckedOutBy { get; set; }
    public DateTimeOffset? CheckedOutAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public long Revision { get; set; } = 1;
    public DateTimeOffset? DeletedAt { get; set; }
    public Guid? DeletedById { get; set; }
    public ICollection<FileVersion> Versions { get; set; } = new List<FileVersion>();
}

public sealed class FileVersion
{
    public Guid Id { get; set; }
    public Guid FileId { get; set; }
    public StoredFile File { get; set; } = null!;
    public int Version { get; set; }
    public string StorageKey { get; set; } = "";
    public long SizeBytes { get; set; }
    public string? Comment { get; set; }
    public Guid CreatedById { get; set; }
    public User CreatedBy { get; set; } = null!;
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class FileAuditEvent
{
    public Guid Id { get; set; }
    public string Action { get; set; } = "";
    public bool IsFolder { get; set; }
    public Guid EntryId { get; set; }
    public string Path { get; set; } = "";
    public string? PreviousPath { get; set; }
    public int? Version { get; set; }
    public long Revision { get; set; }
    public Guid ActorId { get; set; }
    public User Actor { get; set; } = null!;
    public DateTimeOffset CreatedAt { get; set; }
    public string? Details { get; set; }
}

public sealed class ApprovalProcess
{
    public Guid Id { get; set; }
    public Guid FileId { get; set; }
    public StoredFile File { get; set; } = null!;
    public string Title { get; set; } = "";
    public ProcessStatus Status { get; set; } = ProcessStatus.Running;
    public Guid InitiatorId { get; set; }
    public User Initiator { get; set; } = null!;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public ICollection<ApprovalTask> Tasks { get; set; } = new List<ApprovalTask>();
}

public sealed class ApprovalTask
{
    public Guid Id { get; set; }
    public Guid ProcessId { get; set; }
    public ApprovalProcess Process { get; set; } = null!;
    public int StepOrder { get; set; }
    public Guid AssigneeId { get; set; }
    public User Assignee { get; set; } = null!;
    public ApprovalTaskStatus Status { get; set; } = ApprovalTaskStatus.Waiting;
    public string? Comment { get; set; }
    public DateTimeOffset? DueAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
}
