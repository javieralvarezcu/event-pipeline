using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using EventPipeline.Core.Entities;

namespace EventPipeline.Web.Features.BrightData;

/// <summary>Options for one scrape run (base URL and dataset are plain AppSettings).</summary>
public record BrightDataOptions(string BaseUrl, string DatasetId, int PostsPerAccount);

/// <summary>Result of a scrape run: mapped posts plus per-account errors (username → message).</summary>
public record BrightDataFetchResult(
    List<InstagramPost> Posts,
    Dictionary<string, string> AccountErrors);

/// <summary>
/// Snapshot shape returned by the Bright Data datasets v3 API with
/// custom_output_fields=posts,account.
/// </summary>
public class BrightDataAccountSnapshot
{
    public string? Account { get; set; }

    public List<BrightDataPost> Posts { get; set; } = new();
}

public class BrightDataPost
{
    public string? Id { get; set; }

    public string? Caption { get; set; }

    public DateTime? Datetime { get; set; }

    public string? Url { get; set; }

    public string? ImageUrl { get; set; }
}

public interface IBrightDataService
{
    Task<BrightDataFetchResult> FetchRecentPostsAsync(
        IReadOnlyList<IgAccount> accounts, BrightDataOptions options, string token, CancellationToken ct = default);
}

/// <summary>
/// Instagram scraping through the Bright Data datasets v3 API, faithful to the old
/// n8n workflow: trigger a snapshot per profile, poll until it is no longer
/// running, download it and keep the first N posts per account. Errors are
/// isolated per account so one broken profile does not sink the whole run.
/// </summary>
public class BrightDataService : IBrightDataService
{
    private static readonly SemaphoreSlim ConcurrencyGate = new(3, 3);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<BrightDataService> _logger;

    public BrightDataService(IHttpClientFactory httpClientFactory, ILogger<BrightDataService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    // Virtual so tests can shrink the waits.
    protected virtual TimeSpan InitialDelay => TimeSpan.FromSeconds(15);

    protected virtual TimeSpan PollInterval => TimeSpan.FromSeconds(20);

    protected virtual TimeSpan PollDeadline => TimeSpan.FromMinutes(6);

    public async Task<BrightDataFetchResult> FetchRecentPostsAsync(
        IReadOnlyList<IgAccount> accounts, BrightDataOptions options, string token, CancellationToken ct = default)
    {
        var posts = new List<InstagramPost>();
        var errors = new Dictionary<string, string>();

        var tasks = accounts.Select(async account =>
        {
            await ConcurrencyGate.WaitAsync(ct);
            try
            {
                var fetched = await FetchAccountAsync(account, options, token, ct);
                lock (posts)
                {
                    posts.AddRange(fetched);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                lock (errors)
                {
                    errors[account.Username] = ex.Message;
                }
            }
            finally
            {
                ConcurrencyGate.Release();
            }
        });

        await Task.WhenAll(tasks);
        return new BrightDataFetchResult(posts, errors);
    }

    private async Task<List<InstagramPost>> FetchAccountAsync(
        IgAccount account, BrightDataOptions options, string token, CancellationToken ct)
    {
        var client = _httpClientFactory.CreateClient("BrightData");

        var snapshotId = await TriggerSnapshotAsync(client, account.ProfileUrl, options, token, ct);
        await WaitUntilReadyAsync(client, options.BaseUrl, snapshotId, token, ct);
        var snapshots = await DownloadSnapshotAsync(client, options.BaseUrl, snapshotId, token, ct);

        var accountSnapshot = snapshots.FirstOrDefault(s =>
            string.Equals(s.Account, account.Username, StringComparison.OrdinalIgnoreCase))
            ?? snapshots.FirstOrDefault(s => s.Posts.Count > 0);

        if (accountSnapshot == null)
        {
            _logger.LogWarning("BrightData {Account}: snapshot {Snapshot} sin posts (elementos: {Count})",
                account.Username, snapshotId, snapshots.Count);
            return new List<InstagramPost>();
        }

        _logger.LogInformation("BrightData {Account}: {Posts} posts en el snapshot",
            account.Username, accountSnapshot.Posts.Count);
        return accountSnapshot.Posts
            .Take(options.PostsPerAccount)
            .Select(post => new InstagramPost
            {
                Account = account.Username,
                PostId = post.Id ?? string.Empty,
                Caption = post.Caption,
                Datetime = post.Datetime,
                Url = post.Url ?? string.Empty,
                ImageUrl = post.ImageUrl,
            })
            .ToList();
    }

    private async Task<string> TriggerSnapshotAsync(
        HttpClient client, string profileUrl, BrightDataOptions options, string token, CancellationToken ct)
    {
        var query = $"?dataset_id={Uri.EscapeDataString(options.DatasetId)}" +
                    "&notify=false&include_errors=true&custom_output_fields=posts%2Caccount";
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{options.BaseUrl}/datasets/v3/trigger{query}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Content = new StringContent(
            JsonSerializer.Serialize(new
            {
                input = new[] { new { url = profileUrl } },
                limit_per_input = 20,
            }),
            Encoding.UTF8, "application/json");

        using var response = await client.SendAsync(request, ct);
        await EnsureSuccessAsync(response, "trigger", ct);

        var body = await response.Content.ReadAsStringAsync(ct);
        using var json = JsonDocument.Parse(body);
        var snapshotId = json.RootElement.TryGetProperty("snapshot_id", out var id)
            ? id.GetString()
            : null;

        if (string.IsNullOrWhiteSpace(snapshotId))
            throw new InvalidOperationException($"Bright Data trigger no devolvió snapshot_id: {body}");

        _logger.LogInformation("BrightData trigger {Url} → snapshot {Snapshot} (respuesta: {Body})",
            profileUrl, snapshotId, Truncate(body));
        return snapshotId;
    }

    private async Task WaitUntilReadyAsync(
        HttpClient client, string baseUrl, string snapshotId, string token, CancellationToken ct)
    {
        // First poll comes after a short fixed delay (the old workflow waited 180 s;
        // polling is cheaper and breaks early once the snapshot is done).
        await Task.Delay(InitialDelay, ct);

        var deadline = DateTime.UtcNow + PollDeadline;
        while (DateTime.UtcNow < deadline)
        {
            string body;
            try
            {
                using var request = new HttpRequestMessage(
                    HttpMethod.Get, $"{baseUrl}/datasets/v3/progress/{snapshotId}");
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                using var response = await client.SendAsync(request, ct);
                await EnsureSuccessAsync(response, "progress", ct);
                body = await response.Content.ReadAsStringAsync(ct);
            }
            catch (HttpRequestException)
            {
                // The progress entry may be gone while the snapshot is already
                // downloadable — the download itself is the source of truth.
                return;
            }

            _logger.LogInformation("BrightData progress {Snapshot}: {Body}", snapshotId, Truncate(body));
            if (!body.Contains("running", StringComparison.OrdinalIgnoreCase))
                return;

            await Task.Delay(PollInterval, ct);
        }

        throw new TimeoutException($"Snapshot {snapshotId} no terminó en {PollDeadline.TotalMinutes:0} min.");
    }

    private async Task<List<BrightDataAccountSnapshot>> DownloadSnapshotAsync(
        HttpClient client, string baseUrl, string snapshotId, string token, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get, $"{baseUrl}/datasets/v3/snapshot/{snapshotId}?format=json");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await client.SendAsync(request, ct);
        await EnsureSuccessAsync(response, "download", ct);

        var body = await response.Content.ReadAsStringAsync(ct);
        _logger.LogInformation("BrightData snapshot {Snapshot}: HTTP {StatusCode}, {Bytes} bytes, body: {Body}",
            snapshotId, (int)response.StatusCode, body.Length, Truncate(body));

        // Tolerant parsing: array of account snapshots, a single object, or {"data": [...]}.
        // Bright Data emits the JSON keys in lowercase/snake_case ("posts", "caption",
        // "datetime", "image_url"…). System.Text.Json is case-sensitive and does not
        // normalize snake_case by default, so the options below are what actually map
        // them onto the PascalCase model properties.
        var options = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        };
        try
        {
            return JsonSerializer.Deserialize<List<BrightDataAccountSnapshot>>(body, options)
                   ?? new List<BrightDataAccountSnapshot>();
        }
        catch (JsonException)
        {
        }

        try
        {
            var single = JsonSerializer.Deserialize<BrightDataAccountSnapshot>(body, options);
            if (single != null)
                return new List<BrightDataAccountSnapshot> { single };
        }
        catch (JsonException)
        {
        }

        try
        {
            using var json = JsonDocument.Parse(body);
            if (json.RootElement.TryGetProperty("data", out var data))
            {
                return JsonSerializer.Deserialize<List<BrightDataAccountSnapshot>>(data.GetRawText(), options)
                       ?? new List<BrightDataAccountSnapshot>();
            }
        }
        catch (JsonException)
        {
        }

        throw new InvalidOperationException($"Formato de snapshot de Bright Data no reconocido: {Truncate(body)}");
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, string step, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode)
            return;

        var body = await response.Content.ReadAsStringAsync(ct);
        throw new HttpRequestException(
            $"Bright Data {step} devolvió {(int)response.StatusCode}: {Truncate(body)}");
    }

    private static string Truncate(string text) =>
        text.Length <= 300 ? text : text[..300] + "…";
}
