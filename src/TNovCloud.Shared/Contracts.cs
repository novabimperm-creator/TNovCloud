namespace TNovCloud.Shared;

public enum FileDocStatus
{
    Draft,
    InReview,
    Approved,
    Rejected
}

public enum ProcessStatus
{
    Running,
    Completed,
    Rejected,
    Cancelled
}

public enum ApprovalTaskStatus
{
    Waiting,
    Pending,
    Approved,
    Rejected
}

public sealed record UserDto(Guid Id, string Login, string DisplayName, string? Email);

public sealed record LoginRequest(string Login, string Password);

public sealed record LoginResponse(string Token, UserDto User);

public sealed record FsEntryDto(
    bool IsFolder,
    Guid Id,
    Guid? ParentId,
    string Name,
    string Path,
    int? Version,
    FileDocStatus? Status,
    Guid? CheckedOutById,
    string? CheckedOutByName,
    long? SizeBytes,
    DateTimeOffset UpdatedAt,
    long Revision = 0);

public sealed record FileVersionDto(
    int Version,
    long SizeBytes,
    string? Comment,
    string CreatedByName,
    DateTimeOffset CreatedAt);

public sealed record FileDetailsDto(
    FsEntryDto File,
    IReadOnlyList<FileVersionDto> Versions);

public sealed record FsMetadataDto(
    bool IsFolder,
    Guid Id,
    Guid? ParentId,
    string Name,
    string Path,
    int? Version,
    FileDocStatus? Status,
    Guid? CheckedOutById,
    string? CheckedOutByName,
    long? SizeBytes,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    long Revision,
    bool IsDeleted,
    DateTimeOffset? DeletedAt,
    Guid? DeletedById);

public sealed record DirectoryChildrenDto(
    FsMetadataDto Directory,
    IReadOnlyList<FsMetadataDto> Children,
    string? NextCursor = null);

public sealed record RenameEntryRequest(
    string Path,
    string NewName,
    long ExpectedRevision);

public sealed record MoveEntryRequest(
    string Path,
    string DestinationDirectory,
    long ExpectedRevision);

public sealed record DeleteEntryRequest(
    string Path,
    long ExpectedRevision);

public sealed record RestoreDeletedEntryRequest(
    string Path,
    long ExpectedRevision);

public sealed record AuditEventDto(
    Guid Id,
    string Action,
    bool IsFolder,
    Guid EntryId,
    string Path,
    string? PreviousPath,
    int? Version,
    long Revision,
    Guid ActorId,
    string ActorName,
    DateTimeOffset CreatedAt,
    string? Details);

public sealed record CreateProcessRequest(
    string Path,
    string Title,
    IReadOnlyList<Guid> AssigneeIds,
    DateTimeOffset? DueAt);

public sealed record ProcessTaskDto(
    Guid Id,
    Guid ProcessId,
    int StepOrder,
    Guid AssigneeId,
    string AssigneeName,
    ApprovalTaskStatus Status,
    string? Comment,
    DateTimeOffset? DueAt,
    string FilePath,
    string FileName,
    string Title);

public sealed record ProcessDto(
    Guid Id,
    string Title,
    ProcessStatus Status,
    string FilePath,
    string InitiatorName,
    DateTimeOffset CreatedAt,
    IReadOnlyList<ProcessTaskDto> Tasks);

public sealed record DecideRequest(string Action, string? Comment);
