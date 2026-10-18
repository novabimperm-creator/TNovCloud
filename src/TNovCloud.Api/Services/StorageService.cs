namespace TNovCloud.Api.Services;

public sealed class StorageService(IConfiguration config)
{
    private readonly string _root = config["Storage:Root"]
        ?? Path.Combine(AppContext.BaseDirectory, "data");

    public async Task<(string Key, long Size)> SaveAsync(Guid fileId, int version, Stream content, CancellationToken ct)
    {
        // A unique immutable key prevents concurrent check-ins for the same
        // logical version from overwriting the content that wins the DB race.
        var key = $"{fileId:N}/{version}-{Guid.NewGuid():N}";
        var path = Physical(key);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await using var fs = File.Create(path);
        await content.CopyToAsync(fs, ct);
        await fs.FlushAsync(ct);
        return (key, fs.Length);
    }

    public Stream Open(string storageKey)
    {
        var path = Physical(storageKey);
        if (!File.Exists(path))
            throw new FileNotFoundException("Версия файла не найдена на диске.", path);
        return File.OpenRead(path);
    }

    public async Task<(string Key, long Size)> CopyVersionAsync(string fromKey, Guid fileId, int toVersion, CancellationToken ct)
    {
        await using var src = Open(fromKey);
        return await SaveAsync(fileId, toVersion, src, ct);
    }

    private string Physical(string storageKey)
        => Path.Combine(_root, storageKey.Replace('/', Path.DirectorySeparatorChar));
}
