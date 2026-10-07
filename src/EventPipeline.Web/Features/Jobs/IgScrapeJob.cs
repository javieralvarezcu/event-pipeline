using EventPipeline.Core.Entities;
using EventPipeline.Web.Features.BrightData;
using EventPipeline.Web.Features.IgAccounts;
using EventPipeline.Core.Features.Posts;
using EventPipeline.Web.Features.Settings;
using Hangfire;

namespace EventPipeline.Web.Features.Jobs;

/// <summary>
/// Scheduled pipeline: scrape the enabled Instagram accounts through Bright Data
/// and feed the posts to the recognition service. Equivalent to the old n8n
/// workflow "IG scrape + reconocimiento" (cron 0 0 * * 2,5 UTC by default).
/// </summary>
public class IgScrapeJob
{
    private readonly IJobRunService _jobRuns;
    private readonly ISettingsService _settings;
    private readonly IIgAccountService _igAccounts;
    private readonly IBrightDataService _brightData;
    private readonly IRecognitionService _recognition;
    private readonly IJobActivityHub _activity;
    private readonly ILogger<IgScrapeJob> _logger;

    public IgScrapeJob(
        IJobRunService jobRuns,
        ISettingsService settings,
        IIgAccountService igAccounts,
        IBrightDataService brightData,
        IRecognitionService recognition,
        IJobActivityHub activity,
        ILogger<IgScrapeJob> logger)
    {
        _jobRuns = jobRuns;
        _settings = settings;
        _igAccounts = igAccounts;
        _brightData = brightData;
        _recognition = recognition;
        _activity = activity;
        _logger = logger;
    }

    // Long timeout: ~35 accounts × (trigger + poll) can take a while.
    // Retries capped at 2 because every attempt may pay LLM tokens.
    [DisableConcurrentExecution(timeoutInSeconds: 2 * 60 * 60)]
    [AutomaticRetry(Attempts = 2)]
    public async Task ExecuteAsync(CancellationToken ct, string triggerType = "recurring")
    {
        var run = await _jobRuns.StartAsync("IgScrape", triggerType, ct);
        try
        {
            var deepSeekKey = await _settings.GetSecretAsync(SettingsKeys.DeepSeekApiKey, ct)
                ?? throw new InvalidOperationException(
                    "Configura la API key de DeepSeek en Ajustes antes de ejecutar el scrape de Instagram.");
            var token = await _settings.GetSecretAsync(SettingsKeys.BrightDataToken, ct)
                ?? throw new InvalidOperationException(
                    "Configura el token de Bright Data en Ajustes antes de ejecutar el scrape de Instagram.");

            var baseUrl = await _settings.GetPlainAsync(SettingsKeys.BrightDataBaseUrl, ct)
                ?? SettingsKeys.DefaultBrightDataBaseUrl;
            var datasetId = await _settings.GetPlainAsync(SettingsKeys.BrightDataDatasetId, ct)
                ?? SettingsKeys.DefaultBrightDataDatasetId;
            var postsPerAccount = int.Parse(await _settings.GetPlainAsync(SettingsKeys.IgPostsPerAccount, ct)
                ?? SettingsKeys.DefaultIgPostsPerAccount);

            var accounts = await _igAccounts.GetEnabledAsync(ct);
            if (accounts.Count == 0)
                throw new InvalidOperationException(
                    "No hay cuentas de Instagram habilitadas. Añade o habilita cuentas en la página Cuentas.");

            await _activity.LogAsync(run.Id, "IgScrape",
                $"Scrapeando {accounts.Count} cuentas habilitadas con Bright Data…", ct);

            var fetch = await _brightData.FetchRecentPostsAsync(
                accounts, new BrightDataOptions(baseUrl, datasetId, postsPerAccount), token, ct);

            if (fetch.Posts.Count > 0)
            {
                await _activity.LogAsync(run.Id, "IgScrape",
                    $"{fetch.Posts.Count} posts obtenidos. Reconociendo con DeepSeek…", ct);
            }
            else
            {
                await _activity.LogAsync(run.Id, "IgScrape",
                    "Bright Data no devolvió posts.", ct);
            }

            foreach (var (username, error) in fetch.AccountErrors)
            {
                await _activity.LogAsync(run.Id, "IgScrape", $"⚠ {username}: {error}", ct);
            }

            var recognition = fetch.Posts.Count > 0
                ? await _recognition.RecognizeAsync(fetch.Posts, deepSeekKey, dateRange: null, ct)
                : null;

            var scrapedUsernames = accounts
                .Where(a => !fetch.AccountErrors.ContainsKey(a.Username))
                .Select(a => a.Username)
                .ToList();
            await _igAccounts.MarkScrapedAsync(scrapedUsernames, DateTime.UtcNow, ct);

            if (fetch.AccountErrors.Count > 0)
            {
                _logger.LogWarning("Scrape IG: {Errors} cuentas con error: {Details}",
                    fetch.AccountErrors.Count,
                    string.Join("; ", fetch.AccountErrors.Select(kv => $"{kv.Key}: {kv.Value}")));
            }

            await _activity.LogAsync(run.Id, "IgScrape",
                $"✓ {recognition?.EventsFound ?? 0} eventos encontrados de {fetch.Posts.Count} posts.", ct);

            await _jobRuns.CompleteAsync(run.Id, new
            {
                accounts = accounts.Count,
                posts = fetch.Posts.Count,
                events = recognition?.EventsFound ?? 0,
                errors = fetch.AccountErrors,
            }, ct);
        }
        catch (Exception ex)
        {
            await _jobRuns.FailAsync(run.Id, ex, ct);
            throw;
        }
    }
}
