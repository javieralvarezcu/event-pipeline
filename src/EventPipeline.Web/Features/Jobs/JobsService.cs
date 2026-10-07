using EventPipeline.Web.Features.Settings;
using Hangfire;
using Hangfire.Common;
using Hangfire.Storage;

namespace EventPipeline.Web.Features.Jobs;

/// <summary>Job card shown in the Jobs page.</summary>
public record JobDefinitionDto(
    string JobId,
    string DisplayName,
    string Description,
    string Cron,
    bool Enabled,
    DateTime? NextExecutionUtc);

/// <summary>
/// Registers the recurring jobs at startup (idempotent) and manages them from the
/// UI: the cron expressions live in AppSettings so user edits survive restarts,
/// Hangfire owns the actual schedules. All crons are UTC, matching the old n8n
/// workflows.
/// </summary>
public class JobsService
{
    private static readonly (string JobId, string DisplayName, string Description, string CronKey, string DefaultCron)[] Definitions =
    {
        ("ig-scrape", "Scrape IG + reconocimiento",
            "Scrapea los posts recientes de las cuentas de Instagram habilitadas (Bright Data) y los pasa al reconocimiento con DeepSeek.",
            SettingsKeys.JobsIgScrapeCron, SettingsKeys.DefaultIgScrapeCron),
        ("muxo-sync-crosscheck", "Muxo sync + crosscheck",
            "Scrapea muxojaleo.com y cruza los eventos con los nuestros mediante DeepSeek.",
            SettingsKeys.JobsMuxoCron, SettingsKeys.DefaultMuxoCron),
        ("monthly-cleanup", "Cleanup mensual",
            "Busca y elimina eventos duplicados del mes anterior con DeepSeek.",
            SettingsKeys.JobsCleanupCron, SettingsKeys.DefaultCleanupCron),
    };

    private readonly ISettingsService _settings;
    private readonly IBackgroundJobClient _backgroundJobs;

    public JobsService(ISettingsService settings, IBackgroundJobClient backgroundJobs)
    {
        _settings = settings;
        _backgroundJobs = backgroundJobs;
    }

    /// <summary>Registers the recurring jobs with their configured (or default) crons. Idempotent.</summary>
    public async Task EnsureRecurringJobsAsync(CancellationToken ct = default)
    {
        foreach (var (jobId, _, _, cronKey, defaultCron) in Definitions)
        {
            var cron = await _settings.GetPlainAsync(cronKey, ct);
            if (cron == null)
            {
                await _settings.SetPlainAsync(cronKey, defaultCron, ct);
                cron = defaultCron;
            }

            AddOrUpdateRecurring(jobId, cron);
        }
    }

    public async Task<string?> SaveCronAsync(string jobId, string cron, CancellationToken ct = default)
    {
        var definition = Definitions.FirstOrDefault(d => d.JobId == jobId);
        if (definition.JobId == null)
            return "Job desconocido.";

        cron = cron.Trim();
        try
        {
            // RecurringJob.AddOrUpdate parses the cron eagerly and throws
            // ArgumentException (wrapping the Cronos format error) on invalid
            // expressions — validation for free.
            AddOrUpdateRecurring(jobId, cron);
        }
        catch (ArgumentException)
        {
            return "Expresión cron no válida. Formato: 5 campos (minuto hora día mes día-semana), ej. '45 0 * * *'.";
        }

        await _settings.SetPlainAsync(definition.CronKey, cron, ct);
        return null;
    }

    public async Task<string?> SetEnabledAsync(string jobId, bool enabled, CancellationToken ct = default)
    {
        var definition = Definitions.FirstOrDefault(d => d.JobId == jobId);
        if (definition.JobId == null)
            return "Job desconocido.";

        if (enabled)
        {
            var cron = await _settings.GetPlainAsync(definition.CronKey, ct) ?? definition.DefaultCron;
            AddOrUpdateRecurring(jobId, cron);
        }
        else
        {
            RecurringJob.RemoveIfExists(jobId);
        }

        return null;
    }

    public string? TriggerNow(string jobId)
    {
        switch (jobId)
        {
            case "ig-scrape":
                _backgroundJobs.Enqueue<IgScrapeJob>(j => j.ExecuteAsync(CancellationToken.None, "manual"));
                return null;
            case "muxo-sync-crosscheck":
                _backgroundJobs.Enqueue<MuxoJob>(j => j.ExecuteAsync(CancellationToken.None, "manual"));
                return null;
            case "monthly-cleanup":
                _backgroundJobs.Enqueue<CleanupJob>(j => j.ExecuteAsync(CancellationToken.None, "manual"));
                return null;
            default:
                return "Job desconocido.";
        }
    }

    public async Task<List<JobDefinitionDto>> GetJobDefinitionsAsync(CancellationToken ct = default)
    {
        var recurring = JobStorage.Current.GetConnection().GetRecurringJobs()
            .ToDictionary(j => j.Id, j => j);

        var result = new List<JobDefinitionDto>();
        foreach (var (jobId, displayName, description, cronKey, defaultCron) in Definitions)
        {
            var cron = await _settings.GetPlainAsync(cronKey, ct) ?? defaultCron;
            recurring.TryGetValue(jobId, out var job);
            result.Add(new JobDefinitionDto(
                jobId, displayName, description, cron,
                Enabled: job != null,
                NextExecutionUtc: job?.NextExecution));
        }

        return result;
    }

    private static void AddOrUpdateRecurring(string jobId, string cron)
    {
        var options = new RecurringJobOptions { TimeZone = TimeZoneInfo.Utc };
        switch (jobId)
        {
            case "ig-scrape":
                RecurringJob.AddOrUpdate<IgScrapeJob>(jobId, j => j.ExecuteAsync(CancellationToken.None, "recurring"), cron, options);
                break;
            case "muxo-sync-crosscheck":
                RecurringJob.AddOrUpdate<MuxoJob>(jobId, j => j.ExecuteAsync(CancellationToken.None, "recurring"), cron, options);
                break;
            case "monthly-cleanup":
                RecurringJob.AddOrUpdate<CleanupJob>(jobId, j => j.ExecuteAsync(CancellationToken.None, "recurring"), cron, options);
                break;
        }
    }
}
