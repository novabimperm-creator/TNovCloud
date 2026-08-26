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
    DateTimeOffset UpdatedAt);

public sealed record FileVersionDto(
    int Version,
    long SizeBytes,
    string? Comment,
    string CreatedByName,
    DateTimeOffset CreatedAt);

public sealed record FileDetailsDto(
    FsEntryDto File,
    IReadOnlyList<FileVersionDto> Versions);

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
