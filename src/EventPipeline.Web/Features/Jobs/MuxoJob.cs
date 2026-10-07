using EventPipeline.Core.Features.Muxo;
using EventPipeline.Web.Features.Settings;
using Hangfire;

namespace EventPipeline.Web.Features.Jobs;

/// <summary>
/// Scheduled pipeline: scrape muxojaleo.com and crosscheck the result against our
/// events with the LLM. Equivalent to the old n8n workflow "muxo sync + crosscheck"
/// (cron 45 0 * * * UTC by default).
/// </summary>
public class MuxoJob
{
    private readonly IJobRunService _jobRuns;
    private readonly ISettingsService _settings;
    private readonly IMuxoSyncService _muxoSync;
    private readonly ICrossCheckService _crossCheck;
    private readonly IJobActivityHub _activity;
    private readonly ILogger<MuxoJob> _logger;

    public MuxoJob(
        IJobRunService jobRuns,
        ISettingsService settings,
        IMuxoSyncService muxoSync,
        ICrossCheckService crossCheck,
        IJobActivityHub activity,
        ILogger<MuxoJob> logger)
    {
        _jobRuns = jobRuns;
        _settings = settings;
        _muxoSync = muxoSync;
        _crossCheck = crossCheck;
        _activity = activity;
        _logger = logger;
    }

    // Retries capped at 2 because the crosscheck pays LLM tokens.
    [DisableConcurrentExecution(timeoutInSeconds: 30 * 60)]
    [AutomaticRetry(Attempts = 2)]
    public async Task ExecuteAsync(CancellationToken ct, string triggerType = "recurring")
    {
        var run = await _jobRuns.StartAsync("Muxo", triggerType, ct);
        try
        {
            await _activity.LogAsync(run.Id, "Muxo", "Raspando muxojaleo.com (2 meses)…", ct);
            var sync = await _muxoSync.SyncAsync(monthsAhead: 2, ct);

            await _activity.LogAsync(run.Id, "Muxo",
                $"{sync.MuxoEventsScraped} eventos raspados ({sync.MuxoEventsNew} nuevos, {sync.MuxoEventsUpdated} actualizados).", ct);

            var deepSeekKey = await _settings.GetSecretAsync(SettingsKeys.DeepSeekApiKey, ct)
                ?? throw new InvalidOperationException(
                    "Configura la API key de DeepSeek en Ajustes antes del crosscheck.");
            await _activity.LogAsync(run.Id, "Muxo", "Cruzando con muxojaleo mediante DeepSeek…", ct);
            var crosscheck = await _crossCheck.CrossCheckAsync(deepSeekKey, ct);

            _logger.LogInformation(
                "Muxo job: {Scraped} scrapeados ({New} nuevos, {Updated} actualizados), {Matches} cruces nuevos.",
                sync.MuxoEventsScraped, sync.MuxoEventsNew, sync.MuxoEventsUpdated, crosscheck.MatchesFound);

            await _activity.LogAsync(run.Id, "Muxo",
                $"✓ {crosscheck.MatchesFound} cruces nuevos.", ct);

            await _jobRuns.CompleteAsync(run.Id, new
            {
                muxoScraped = sync.MuxoEventsScraped,
                muxoNew = sync.MuxoEventsNew,
                muxoUpdated = sync.MuxoEventsUpdated,
                muxoConsidered = crosscheck.MuxoEventsConsidered,
                ourAnalyzed = crosscheck.OurEventsAnalyzed,
                matches = crosscheck.MatchesFound,
            }, ct);
        }
        catch (Exception ex)
        {
            await _jobRuns.FailAsync(run.Id, ex, ct);
            throw;
        }
    }
}
