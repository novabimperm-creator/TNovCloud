using System.Text.Json;
using System.Text.Json.Serialization;

namespace TNovCloud.FileSystemHost;

internal sealed class HostSession
{
    public string ServerUrl { get; set; } = "http://localhost:5080";
    public string DriveLetter { get; set; } = "T";
    public string? Token { get; set; }
    public SessionUser? User { get; set; }

    public static HostSession Load(string path)
    {
        if (!File.Exists(path))
            throw new InvalidOperationException($"Session file does not exist: {path}");

        var result = JsonSerializer.Deserialize<HostSession>(
            File.ReadAllText(path),
            JsonDefaults.Options);
        if (result?.User is null || string.IsNullOrWhiteSpace(result.Token))
            throw new InvalidOperationException("TNovCloud session is not logged in.");
        if (!Uri.TryCreate(result.ServerUrl, UriKind.Absolute, out _))
            throw new InvalidOperationException("TNovCloud session contains an invalid ServerUrl.");
        return result;
    }
}

internal sealed record SessionUser(Guid Id, string Login, string DisplayName, string? Email);

internal sealed record FsEntryDto(
    bool IsFolder,
    Guid Id,
    Guid? ParentId,
    string Name,
    string Path,
    int? Version,
    JsonElement? Status,
    Guid? CheckedOutById,
    string? CheckedOutByName,
    long? SizeBytes,
    DateTimeOffset UpdatedAt,
    long Revision);

internal sealed class FsMetadataDto
{
    public bool IsFolder { get; set; }
    public string Path { get; set; } = "";
    public string Name { get; set; } = "";
    public long Revision { get; set; }
}

internal enum EntryState
{
    Remote,
    New,
    Dirty,
    Deleted,
    Renamed
}

internal sealed record ManifestEntry(
    string Path,
    bool IsFolder,
    Guid? RemoteId,
    int? Version,
    Guid? CheckedOutById,
    long SizeBytes,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    EntryState State,
    string? OriginalPath)
{
    public bool IsRemote => RemoteId.HasValue;
    public long Revision { get; init; }
    public DateTimeOffset AccessedAt { get; init; }
}

internal static class HostPaths
{
    private static string? _appRoot;

    public static string AppRoot => _appRoot ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "TNovCloud");

    public static string SessionPath => Path.Combine(AppRoot, "session.json");
    public static string HostRoot => Path.Combine(AppRoot, "FileSystemHost");
    public static string CacheRoot => Path.Combine(HostRoot, "cache");
    public static string DatabasePath => Path.Combine(HostRoot, "manifest.db");
    public static string LogPath => Path.Combine(HostRoot, "host.log");
    public static string PipeName => $"TNovCloud.FileSystemHost.{Sanitize(Environment.UserName)}";
    public static string MutexName => $"Local\\TNovCloud.FileSystemHost.{Sanitize(Environment.UserName)}";

    public static void Configure(string? appRoot)
    {
        if (string.IsNullOrWhiteSpace(appRoot))
            return;
        _appRoot = Path.GetFullPath(appRoot);
    }

    private static string Sanitize(string value)
        => string.Concat(value.Select(c => char.IsLetterOrDigit(c) ? c : '_'));
}

internal static class JsonDefaults
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() }
    };
}
