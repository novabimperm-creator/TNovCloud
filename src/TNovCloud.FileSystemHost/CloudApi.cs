using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace TNovCloud.FileSystemHost;

internal sealed class CloudApi : IDisposable
{
    private readonly HttpClient _http;

    public CloudApi(HostSession session)
    {
        _http = new HttpClient
        {
            BaseAddress = new Uri(session.ServerUrl.TrimEnd('/') + "/"),
            Timeout = TimeSpan.FromMinutes(10)
        };
        _http.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", session.Token);
    }

    public async Task<IReadOnlyList<FsEntryDto>> GetTreeAsync(CancellationToken cancellationToken)
    {
        using var response = await _http.GetAsync("api/fs/tree", cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<List<FsEntryDto>>(
                   JsonDefaults.Options,
                   cancellationToken)
               ?? [];
    }

    public async Task DownloadAsync(
        string remotePath,
        string destination,
        CancellationToken cancellationToken)
    {
        using var response = await _http.GetAsync(
            $"api/fs/content?path={Query(remotePath)}",
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);

        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".download";
        try
        {
            await using (var output = new FileStream(
                             temporary,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             128 * 1024,
                             FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await response.Content.CopyToAsync(output, cancellationToken);
            }

            File.Move(temporary, destination, true);
        }
        finally
        {
            File.Delete(temporary);
        }
    }

    public async Task CreateDirectoryAsync(string remotePath, CancellationToken cancellationToken)
    {
        using var response = await _http.PostAsync(
            $"api/fs/mkdir?path={Query(remotePath)}",
            null,
            cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
    }

    public async Task UploadNewAsync(
        string remotePath,
        string localPath,
        CancellationToken cancellationToken)
    {
        using var form = new MultipartFormDataContent();
        await using var stream = new FileStream(
            localPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        form.Add(new StreamContent(stream), "file", Path.GetFileName(localPath));
        form.Add(new StringContent("Added through WinFsp volume"), "comment");

        using var response = await _http.PostAsync(
            $"api/fs/upload?path={Query(remotePath)}",
            form,
            cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
    }

    public async Task RenameAsync(
        string remotePath,
        string newPath,
        CancellationToken cancellationToken)
    {
        var metadata = await GetMetadataAsync(remotePath, cancellationToken);
        var oldParent = ManifestStore.Parent(remotePath);
        var newParent = ManifestStore.Parent(newPath);
        var currentPath = remotePath;

        if (!string.Equals(oldParent, newParent, StringComparison.OrdinalIgnoreCase))
        {
            metadata = await PostJsonAsync<FsMetadataDto>(
                "api/fs/move",
                new
                {
                    path = currentPath,
                    destinationDirectory = newParent,
                    expectedRevision = metadata.Revision
                },
                cancellationToken);
            currentPath = metadata.Path;
        }

        var newName = Path.GetFileName(newPath.Replace('/', '\\'));
        if (!string.Equals(metadata.Name, newName, StringComparison.Ordinal))
        {
            _ = await PostJsonAsync<FsMetadataDto>(
                "api/fs/rename",
                new
                {
                    path = currentPath,
                    newName,
                    expectedRevision = metadata.Revision
                },
                cancellationToken);
        }
    }

    public async Task DeleteAsync(string remotePath, CancellationToken cancellationToken)
    {
        var metadata = await GetMetadataAsync(remotePath, cancellationToken);
        using var response = await _http.PostAsJsonAsync(
            "api/fs/delete",
            new { path = remotePath, expectedRevision = metadata.Revision },
            JsonDefaults.Options,
            cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
    }

    private async Task<FsMetadataDto> GetMetadataAsync(
        string remotePath,
        CancellationToken cancellationToken)
    {
        using var response = await _http.GetAsync(
            $"api/fs/metadata?path={Query(remotePath)}&includeDeleted=false",
            cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<FsMetadataDto>(
                   JsonDefaults.Options,
                   cancellationToken)
               ?? throw new HttpRequestException("TNovCloud API returned empty metadata.");
    }

    private async Task<T> PostJsonAsync<T>(
        string relativeUrl,
        object body,
        CancellationToken cancellationToken)
    {
        using var response = await _http.PostAsJsonAsync(
            relativeUrl,
            body,
            JsonDefaults.Options,
            cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<T>(
                   JsonDefaults.Options,
                   cancellationToken)
               ?? throw new HttpRequestException("TNovCloud API returned an empty response.");
    }

    private static async Task EnsureSuccessAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
            return;

        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        try
        {
            using var json = JsonDocument.Parse(body);
            if (json.RootElement.TryGetProperty("error", out var error))
                body = error.GetString() ?? body;
        }
        catch (JsonException)
        {
            // Keep the original response body.
        }

        throw new HttpRequestException(
            $"TNovCloud API returned {(int)response.StatusCode}: {body}",
            null,
            response.StatusCode);
    }

    private static string Query(string value)
        => Uri.EscapeDataString(value.Replace('\\', '/').Trim('/'));

    public void Dispose() => _http.Dispose();

}
