using EventPipeline.Web.Features.Jobs;
using EventPipeline.Web.Features.Settings;
using Hangfire;
using Hangfire.MemoryStorage;
using Hangfire.Storage;

namespace EventPipeline.Tests;

/// <summary>Hangfire's JobStorage.Current is global: these tests must run serially.</summary>
[CollectionDefinition("Hangfire serial", DisableParallelization = true)]
public class HangfireSerialCollection
{
}

[Collection("Hangfire serial")]
public class JobsTests : IDisposable
{
    private readonly MemoryStorage _storage;

    public JobsTests()
    {
        _storage = new MemoryStorage();
        JobStorage.Current = _storage;
    }

    public void Dispose()
    {
        JobStorage.Current = null;
    }

    private static JobsService CreateService(FakeSettingsService settings) =>
        new(settings, new BackgroundJobClient(JobStorage.Current));

    [Fact]
    public async Task EnsureRecurringJobs_RegistersTheThreeJobsWithDefaultCrons()
    {
        var settings = new FakeSettingsService();
        var service = CreateService(settings);

        await service.EnsureRecurringJobsAsync();

        var recurring = JobStorage.Current.GetConnection().GetRecurringJobs();
        Assert.Equal(3, recurring.Count);
        Assert.Equal(SettingsKeys.DefaultIgScrapeCron, recurring.Single(j => j.Id == "ig-scrape").Cron);
        Assert.Equal(SettingsKeys.DefaultMuxoCron, recurring.Single(j => j.Id == "muxo-sync-crosscheck").Cron);
        Assert.Equal(SettingsKeys.DefaultCleanupCron, recurring.Single(j => j.Id == "monthly-cleanup").Cron);

        // Defaults got persisted so user edits in the UI have a starting point.
        Assert.Equal(SettingsKeys.DefaultIgScrapeCron, await settings.GetPlainAsync(SettingsKeys.JobsIgScrapeCron));
    }

    [Fact]
    public async Task EnsureRecurringJobs_UsesUserEditedCrons()
    {
        var settings = new FakeSettingsService(new[]
        {
            (SettingsKeys.JobsIgScrapeCron, "30 6 * * 1"),
        });
        var service = CreateService(settings);

        await service.EnsureRecurringJobsAsync();

        var recurring = JobStorage.Current.GetConnection().GetRecurringJobs();
        Assert.Equal("30 6 * * 1", recurring.Single(j => j.Id == "ig-scrape").Cron);
        Assert.Equal(SettingsKeys.DefaultMuxoCron, recurring.Single(j => j.Id == "muxo-sync-crosscheck").Cron);
    }

    [Fact]
    public async Task SaveCron_ValidatesAndPersists()
    {
        var settings = new FakeSettingsService();
        var service = CreateService(settings);
        await service.EnsureRecurringJobsAsync();

        Assert.NotNull(await service.SaveCronAsync("ig-scrape", "esto no es un cron"));
        Assert.Equal(SettingsKeys.DefaultIgScrapeCron, await settings.GetPlainAsync(SettingsKeys.JobsIgScrapeCron));

        Assert.Null(await service.SaveCronAsync("ig-scrape", "15 3 * * 5"));
        Assert.Equal("15 3 * * 5", await settings.GetPlainAsync(SettingsKeys.JobsIgScrapeCron));
        Assert.Equal("15 3 * * 5",
            JobStorage.Current.GetConnection().GetRecurringJobs().Single(j => j.Id == "ig-scrape").Cron);
    }

    [Fact]
    public async Task SetEnabled_RemovesAndReaddsTheRecurringJob()
    {
        var settings = new FakeSettingsService();
        var service = CreateService(settings);
        await service.EnsureRecurringJobsAsync();

        Assert.Null(await service.SetEnabledAsync("monthly-cleanup", enabled: false));
        Assert.Empty(JobStorage.Current.GetConnection().GetRecurringJobs()
            .Where(j => j.Id == "monthly-cleanup"));

        var definitions = await service.GetJobDefinitionsAsync();
        Assert.False(definitions.Single(d => d.JobId == "monthly-cleanup").Enabled);

        Assert.Null(await service.SetEnabledAsync("monthly-cleanup", enabled: true));
        Assert.Single(JobStorage.Current.GetConnection().GetRecurringJobs()
            .Where(j => j.Id == "monthly-cleanup"));
    }

    [Fact]
    public async Task TriggerNow_EnqueuesAManualRun()
    {
        var settings = new FakeSettingsService();
        var service = CreateService(settings);
        await service.EnsureRecurringJobsAsync();

        Assert.Null(service.TriggerNow("muxo-sync-crosscheck"));
        Assert.Equal("Job desconocido.", service.TriggerNow("no-existe"));

        var enqueued = _storage.GetMonitoringApi().EnqueuedJobs("default", 0, 10);
        Assert.Single(enqueued);
        Assert.Equal("MuxoJob", enqueued[0].Value.Job.Type.Name);
    }

    [Fact]
    public async Task GetJobDefinitions_ReturnsCardsWithCronAndEnabledState()
    {
        var settings = new FakeSettingsService();
        var service = CreateService(settings);
        await service.EnsureRecurringJobsAsync();

        var definitions = await service.GetJobDefinitionsAsync();

        Assert.Equal(3, definitions.Count);
        var muxo = definitions.Single(d => d.JobId == "muxo-sync-crosscheck");
        Assert.Equal("Muxo sync + crosscheck", muxo.DisplayName);
        Assert.Equal(SettingsKeys.DefaultMuxoCron, muxo.Cron);
        Assert.True(muxo.Enabled);
        Assert.NotNull(muxo.NextExecutionUtc);
    }
}
