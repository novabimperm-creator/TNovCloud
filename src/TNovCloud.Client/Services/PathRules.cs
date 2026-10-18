using System.IO;
using TNovCloud.Shared;

namespace TNovCloud.Client.Services;

public static class PathRules
{
    private static readonly HashSet<string> Names = new(StringComparer.OrdinalIgnoreCase)
    {
        "desktop.ini", "thumbs.db", ".ds_store"
    };

    private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".tmp", ".bak", ".dwl", ".dwl2", ".sv$", ".lock"
    };

    public static bool Ignore(string path)
    {
        var name = Path.GetFileName(path);
        if (string.IsNullOrEmpty(name) || Names.Contains(name) || name.StartsWith("~$"))
            return true;
        var ext = Path.GetExtension(name);
        return Extensions.Contains(ext);
    }

    public static string RelativeFromDrive(string fullPath, char letter)
    {
        var full = Path.GetFullPath(fullPath);
        foreach (var root in new[]
                 {
                     WithSlash(Path.GetFullPath($"{letter}:\\")),
                     WithSlash(Path.GetFullPath(SessionStore.DrivePath))
                 })
        {
            if (full.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                return full[root.Length..].Replace('\\', '/');
        }

        throw new InvalidOperationException("Файл не на диске TNovCloud.");
    }

    private static string WithSlash(string path) => path.TrimEnd('\\') + "\\";

    public static string LocalPath(string relative)
        => Path.Combine(SessionStore.DrivePath, relative.Replace('/', Path.DirectorySeparatorChar));

    public static void SetWritable(string path, bool writable)
    {
        if (!File.Exists(path))
            return;
        var attr = File.GetAttributes(path);
        if (writable)
            File.SetAttributes(path, attr & ~FileAttributes.ReadOnly);
        else
            File.SetAttributes(path, attr | FileAttributes.ReadOnly);
    }
}
