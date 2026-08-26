using System.Text.Json;
using TNovCloud.Shared;

namespace TNovCloud.Client.Services;

public sealed class SyncService(CloudApiClient api)
{
    private static readonly string IndexPath = Path.Combine(SessionStore.Root, "versions.json");

    public async Task SyncAsync(Guid currentUserId, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        Directory.CreateDirectory(SessionStore.DrivePath);
        var index = LoadIndex();
        progress?.Report("Загрузка дерева…");
        var tree = await api.TreeAsync(ct);
        var serverFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in tree.Where(x => x.IsFolder).OrderBy(x => x.Path.Count(c => c == '/')))
            Directory.CreateDirectory(PathRules.LocalPath(entry.Path));

        foreach (var file in tree.Where(x => !x.IsFolder))
        {
            ct.ThrowIfCancellationRequested();
            serverFiles.Add(file.Path);
            var local = PathRules.LocalPath(file.Path);
            Directory.CreateDirectory(Path.GetDirectoryName(local)!);
            var mine = file.CheckedOutById == currentUserId;
            var serverVersion = file.Version ?? 1;
            index.TryGetValue(file.Path, out var localVersion);
            var missing = !File.Exists(local);
            var stale = !mine && localVersion != serverVersion;

            if (missing || stale)
            {
                progress?.Report($"Скачивание {file.Path}…");
                var temp = local + ".download";
                PathRules.SetWritable(local, true);
                await api.DownloadAsync(file.Path, temp, ct: ct);
                if (File.Exists(local))
                    File.Delete(local);
                File.Move(temp, local, true);
            }

            PathRules.SetWritable(local, mine);
            index[file.Path] = serverVersion;
        }

        await UploadNewLocalFilesAsync(serverFiles, index, ct);
        SaveIndex(index);
        progress?.Report("Синхронизация завершена.");
    }

    private async Task UploadNewLocalFilesAsync(HashSet<string> serverFiles, Dictionary<string, int> index, CancellationToken ct)
    {
        if (!Directory.Exists(SessionStore.DrivePath))
            return;
        foreach (var file in Directory.EnumerateFiles(SessionStore.DrivePath, "*", SearchOption.AllDirectories))
        {
            if (PathRules.Ignore(file) || file.EndsWith(".download", StringComparison.OrdinalIgnoreCase))
                continue;
            var rel = Path.GetRelativePath(SessionStore.DrivePath, file).Replace('\\', '/');
            if (serverFiles.Contains(rel))
                continue;
            await api.UploadAsync(rel, file, "Добавлено из Проводника", ct);
            PathRules.SetWritable(file, false);
            index[rel] = 1;
        }
    }

    public static void RememberVersion(string path, int version)
    {
        var index = LoadIndex();
        index[path.Replace('\\', '/')] = version;
        SaveIndex(index);
    }

    private static Dictionary<string, int> LoadIndex()
    {
        try
        {
            if (File.Exists(IndexPath))
                return JsonSerializer.Deserialize<Dictionary<string, int>>(File.ReadAllText(IndexPath))
                       ?? new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        }
        catch
        {
            // повреждённый индекс — полная повторная загрузка
        }

        return new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
    }

    private static void SaveIndex(Dictionary<string, int> index)
        => File.WriteAllText(IndexPath, JsonSerializer.Serialize(index));
}
