using EventPipeline.Core.Data;
using EventPipeline.Core.Features.Events;
using EventPipeline.Web.Features.Settings;
using Hangfire;
using Microsoft.EntityFrameworkCore;

namespace EventPipeline.Web.Features.Jobs;

/// <summary>
/// Scheduled pipeline: ask the LLM to find and delete duplicate events of the
/// previous month. Equivalent to the old n8n workflow "cleanup mensual"
/// (cron 15 0 1 * * UTC by default). Also purges JobRuns older than 90 days.
/// </summary>
public class CleanupJob
{
    private readonly IJobRunService _jobRuns;
    private readonly ISettingsService _settings;
    private readonly ICleanupService _cleanup;
    private readonly IJobActivityHub _activity;
    private readonly AppDbContext _db;
    private readonly ILogger<CleanupJob> _logger;

    public CleanupJob(
        IJobRunService jobRuns,
        ISettingsService settings,
        ICleanupService cleanup,
        IJobActivityHub activity,
        AppDbContext db,
        ILogger<CleanupJob> logger)
    {
        _jobRuns = jobRuns;
        _settings = settings;
        _cleanup = cleanup;
        _activity = activity;
        _db = db;
        _logger = logger;
    }

    // Retries capped at 2 because every attempt pays LLM tokens.
    [DisableConcurrentExecution(timeoutInSeconds: 30 * 60)]
    [AutomaticRetry(Attempts = 2)]
    public async Task ExecuteAsync(CancellationToken ct, string triggerType = "recurring")
    {
        var run = await _jobRuns.StartAsync("Cleanup", triggerType, ct);
        try
        {
            var deepSeekKey = await _settings.GetSecretAsync(SettingsKeys.DeepSeekApiKey, ct)
                ?? throw new InvalidOperationException(
                    "Configura la API key de DeepSeek en Ajustes antes del cleanup.");

            // Previous calendar month, same arithmetic the n8n workflow used.
            var now = DateTime.UtcNow;
            var previous = new DateTime(now.Year, now.Month, 1).AddMonths(-1);

            await _activity.LogAsync(run.Id, "Cleanup",
                $"Analizando {previous:yyyy-MM} con el LLM (duplicados)…", ct);

            var result = await _cleanup.CleanupMonthAsync(previous.Year, previous.Month, deepSeekKey, ct);

            _logger.LogInformation(
                "Cleanup {Month:yyyy-MM}: {Analyzed} analizados, {Deleted} duplicados eliminados.",
                previous, result.EventsAnalyzed, result.DeletedCount);

            await _activity.LogAsync(run.Id, "Cleanup",
                $"✓ {result.EventsAnalyzed} eventos analizados, {result.DeletedCount} duplicados eliminados.", ct);

            await PurgeOldJobRunsAsync(ct);

            await _jobRuns.CompleteAsync(run.Id, new
            {
                month = $"{previous.Year:0000}-{previous.Month:00}",
                eventsAnalyzed = result.EventsAnalyzed,
                deletedCount = result.DeletedCount,
            }, ct);
        }
        catch (Exception ex)
        {
            await _jobRuns.FailAsync(run.Id, ex, ct);
            throw;
        }
    }

    private async Task PurgeOldJobRunsAsync(CancellationToken ct)
    {
        var cutoff = DateTime.UtcNow.AddDays(-90);
        var old = await _db.JobRuns
            .Where(r => r.CompletedAtUtc != null && r.CompletedAtUtc < cutoff)
            .ExecuteDeleteAsync(ct);

        var oldLogs = await _db.JobRunLogs
            .Where(l => l.LoggedAtUtc < cutoff)
            .ExecuteDeleteAsync(ct);

        if (old > 0 || oldLogs > 0)
            _logger.LogInformation("Cleanup: {Count} JobRuns y {Logs} líneas de log antiguos purgados.", old, oldLogs);
    }
}
