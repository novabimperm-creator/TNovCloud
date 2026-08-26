using System.Text.Json;
using TNovCloud.Shared;

namespace TNovCloud.Client.Services;

public sealed class AppSession
{
    public string ServerUrl { get; set; } = "http://localhost:5080";
    public string DriveLetter { get; set; } = "T";
    public string? Token { get; set; }
    public UserDto? User { get; set; }
}

public static class SessionStore
{
    public static string Root => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TNovCloud");

    public static string DrivePath => Path.Combine(Root, "Drive");
    private static string FilePath => Path.Combine(Root, "session.json");

    public static AppSession Load()
    {
        Directory.CreateDirectory(Root);
        if (!File.Exists(FilePath))
            return new AppSession();
        try
        {
            return JsonSerializer.Deserialize<AppSession>(File.ReadAllText(FilePath)) ?? new AppSession();
        }
        catch
        {
            return new AppSession();
        }
    }

    public static void Save(AppSession session)
    {
        Directory.CreateDirectory(Root);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(session, new JsonSerializerOptions { WriteIndented = true }));
    }
}
