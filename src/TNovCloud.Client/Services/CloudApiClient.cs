using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using TNovCloud.Shared;

namespace TNovCloud.Client.Services;

public sealed class ApiException(string message) : Exception(message);

public sealed class CloudApiClient : IDisposable
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromMinutes(10) };

    public void Configure(string baseUrl, string? token)
    {
        _http.BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/");
        _http.DefaultRequestHeaders.Authorization = string.IsNullOrEmpty(token)
            ? null
            : new AuthenticationHeaderValue("Bearer", token);
    }

    public Task<LoginResponse> LoginAsync(string login, string password, CancellationToken ct = default)
        => PostJson<LoginRequest, LoginResponse>("api/auth/login", new LoginRequest(login, password), ct);

    public async Task<IReadOnlyList<UserDto>> UsersAsync(CancellationToken ct = default)
        => await Get<List<UserDto>>("api/users", ct);

    public async Task<IReadOnlyList<FsEntryDto>> TreeAsync(CancellationToken ct = default)
        => await Get<List<FsEntryDto>>("api/fs/tree", ct);

    public Task MkdirAsync(string path, CancellationToken ct = default)
        => SendAsync(new HttpRequestMessage(HttpMethod.Post, $"api/fs/mkdir?path={Q(path)}"), ct);

    public Task CheckoutAsync(string path, CancellationToken ct = default)
        => SendAsync(new HttpRequestMessage(HttpMethod.Post, $"api/fs/checkout?path={Q(path)}"), ct);

    public Task CancelAsync(string path, CancellationToken ct = default)
        => SendAsync(new HttpRequestMessage(HttpMethod.Post, $"api/fs/cancel?path={Q(path)}"), ct);

    public Task RestoreAsync(string path, int version, CancellationToken ct = default)
        => SendAsync(new HttpRequestMessage(HttpMethod.Post, $"api/fs/restore?path={Q(path)}&version={version}"), ct);

    public Task<FileDetailsDto> DetailsAsync(string path, CancellationToken ct = default)
        => Get<FileDetailsDto>($"api/fs/details?path={Q(path)}", ct);

    public async Task DownloadAsync(string path, string localFile, int? version = null, CancellationToken ct = default)
    {
        var url = $"api/fs/content?path={Q(path)}" + (version is { } v ? $"&version={v}" : "");
        using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        await EnsureSuccess(response, ct);
        Directory.CreateDirectory(Path.GetDirectoryName(localFile)!);
        await using var fs = File.Create(localFile);
        await response.Content.CopyToAsync(fs, ct);
    }

    public Task UploadAsync(string path, string localFile, string? comment, CancellationToken ct = default)
        => UploadAsync("api/fs/upload", path, localFile, comment, keepCheckout: false, ct);

    public Task CheckinAsync(string path, string localFile, string? comment, bool keepCheckout, CancellationToken ct = default)
        => UploadAsync($"api/fs/checkin?keepCheckout={keepCheckout.ToString().ToLowerInvariant()}", path, localFile, comment, keepCheckout, ct);

    public Task<ProcessDto> StartProcessAsync(CreateProcessRequest request, CancellationToken ct = default)
        => PostJson<CreateProcessRequest, ProcessDto>("api/processes", request, ct);

    public async Task<IReadOnlyList<ProcessTaskDto>> InboxAsync(CancellationToken ct = default)
        => await Get<List<ProcessTaskDto>>("api/tasks/inbox", ct);

    public Task<ProcessDto> DecideAsync(Guid taskId, string action, string? comment, CancellationToken ct = default)
        => PostJson<DecideRequest, ProcessDto>($"api/tasks/{taskId}/decide", new DecideRequest(action, comment), ct);

    private async Task UploadAsync(string url, string path, string localFile, string? comment, bool keepCheckout, CancellationToken ct)
    {
        using var form = new MultipartFormDataContent();
        await using var fs = new FileStream(localFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        form.Add(new StreamContent(fs), "file", Path.GetFileName(localFile));
        if (!string.IsNullOrEmpty(comment))
            form.Add(new StringContent(comment), "comment");
        var separator = url.Contains('?') ? "&" : "?";
        using var response = await _http.PostAsync($"{url}{separator}path={Q(path)}", form, ct);
        await EnsureSuccess(response, ct);
    }

    private async Task<T> Get<T>(string url, CancellationToken ct)
    {
        using var response = await _http.GetAsync(url, ct);
        await EnsureSuccess(response, ct);
        return await response.Content.ReadFromJsonAsync<T>(Json, ct) ?? throw new ApiException("Пустой ответ сервера.");
    }

    private async Task<TOut> PostJson<TIn, TOut>(string url, TIn body, CancellationToken ct)
    {
        using var response = await _http.PostAsJsonAsync(url, body, Json, ct);
        await EnsureSuccess(response, ct);
        return await response.Content.ReadFromJsonAsync<TOut>(Json, ct) ?? throw new ApiException("Пустой ответ сервера.");
    }

    private async Task SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        using var response = await _http.SendAsync(request, ct);
        await EnsureSuccess(response, ct);
    }

    private static async Task EnsureSuccess(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode)
            return;
        var text = await response.Content.ReadAsStringAsync(ct);
        try
        {
            var error = JsonSerializer.Deserialize<ErrorBody>(text, Json);
            throw new ApiException(error?.Error ?? text);
        }
        catch (ApiException)
        {
            throw;
        }
        catch
        {
            throw new ApiException(string.IsNullOrWhiteSpace(text) ? $"Ошибка HTTP {(int)response.StatusCode}" : text);
        }
    }

    private static string Q(string path) => Uri.EscapeDataString(path.Replace('\\', '/'));

    public void Dispose() => _http.Dispose();

    private sealed class ErrorBody
    {
        public string? Error { get; set; }
    }
}
