namespace EventPipeline.Web.Features.Settings;

/// <summary>
/// Key names of the AppSettings table. Secret values are encrypted at rest with
/// Data Protection; plain values hold non-sensitive configuration.
/// </summary>
public static class SettingsKeys
{
    // Secrets (encrypted, managed from the Settings page).
    public const string DeepSeekApiKey = "DeepSeekApiKey";
    public const string BrightDataToken = "BrightDataToken";

    // Plain configuration (defaults applied by the seed; editable from the Settings page).
    public const string BrightDataBaseUrl = "BrightDataBaseUrl";
    public const string BrightDataDatasetId = "BrightDataDatasetId";
    public const string IgPostsPerAccount = "IgPostsPerAccount";

    // Cron expressions of the scheduled jobs (UTC, five-field).
    public const string JobsIgScrapeCron = "Jobs:IgScrape:Cron";
    public const string JobsMuxoCron = "Jobs:Muxo:Cron";
    public const string JobsCleanupCron = "Jobs:Cleanup:Cron";

    public const string DefaultBrightDataBaseUrl = "https://api.brightdata.com";
    public const string DefaultBrightDataDatasetId = "gd_l1vikfch901nx3by4";
    public const string DefaultIgPostsPerAccount = "4";

    // Same crons the n8n workflows used (UTC).
    public const string DefaultIgScrapeCron = "0 0 * * 2,5";
    public const string DefaultMuxoCron = "45 0 * * *";
    public const string DefaultCleanupCron = "15 0 1 * *";
}
