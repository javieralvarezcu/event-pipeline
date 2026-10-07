using EventPipeline.Core.Contracts;
using EventPipeline.Core.Entities;
using EventPipeline.Web.Features.BrightData;
using EventPipeline.Core.Features.Events;
using EventPipeline.Web.Features.IgAccounts;
using EventPipeline.Web.Features.Jobs;
using EventPipeline.Core.Features.Muxo;
using EventPipeline.Core.Features.Posts;
using EventPipeline.Web.Features.Settings;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace EventPipeline.Tests;

public class JobExecutionTests
{
    [Fact]
    public async Task IgScrapeJob_ScrapesRecognizesAndSummarizes()
    {
        using var fixture = new TestDatabase();
        var settings = new FakeSettingsService(new (string Key, string? Value)[]
        {
            (SettingsKeys.DeepSeekApiKey, "sk-deepseek"),
            (SettingsKeys.BrightDataToken, "bd-token"),
        });
        var accounts = new FakeIgAccountService(new[]
        {
            new IgAccount { Id = 1, Username = "buena", ProfileUrl = "https://www.instagram.com/buena/", Enabled = true },
            new IgAccount { Id = 2, Username = "mala", ProfileUrl = "https://www.instagram.com/mala/", Enabled = true },
            new IgAccount { Id = 3, Username = "deshabilitada", ProfileUrl = "https://www.instagram.com/deshabilitada/", Enabled = false },
        });
        var brightData = new FakeBrightDataService((_, _, _) => new BrightDataFetchResult(
            new List<InstagramPost>
            {
                TestData.CreatePost("p1", caption: "Concierto el sábado"),
                TestData.CreatePost("p2", caption: "Fiesta el viernes"),
            },
            new Dictionary<string, string> { ["mala"] = "perfil no encontrado" }));
        var recognition = new FakeRecognitionService((posts, key, ct) => Task.FromResult(new RecognitionResponse
        {
            TotalPosts = posts.Count,
            EventsFound = 2,
            Events = new List<RecognizedEventDto>()
        }));
        var activity = new FakeJobActivityHub();
        var jobRuns = new JobRunService(fixture.Db, activity);

        var job = new IgScrapeJob(jobRuns, settings, accounts, brightData, recognition, activity,
            NullLogger<IgScrapeJob>.Instance);
        await job.ExecuteAsync(CancellationToken.None, "manual");

        // El job emite su progreso al log en vivo.
        Assert.Contains(activity.Entries, e => e.Message.Contains("Scrapeando 2 cuentas"));
        Assert.Contains(activity.Entries, e => e.Message.Contains("2 posts obtenidos"));
        Assert.Contains(activity.Entries, e => e.Message.Contains("mala: perfil no encontrado"));
        Assert.Contains(activity.Entries, e => e.Message.Contains("✓ 2 eventos encontrados"));

        var run = await fixture.Db.JobRuns.SingleAsync();
        Assert.Equal("Succeeded", run.Status);
        Assert.Equal("manual", run.TriggerType);
        Assert.Contains("\"events\":2", run.SummaryJson);
        Assert.Contains("\"accounts\":2", run.SummaryJson);
        Assert.Contains("\"posts\":2", run.SummaryJson);
        Assert.Contains("mala", run.SummaryJson);

        // Only the account without errors is marked as scraped.
        Assert.Equal(new[] { "buena" }, accounts.MarkedScrapedUsernames);

        // The recognition got the stored key and no date range (same as the n8n workflow).
        Assert.Null(recognition.LastDateRange);
        Assert.Equal(new[] { "sk-deepseek" }, recognition.ReceivedApiKeys);
    }

    [Fact]
    public async Task IgScrapeJob_FailsWithAClearMessageWhenTheKeyIsMissing()
    {
        using var fixture = new TestDatabase();
        var settings = new FakeSettingsService(new (string Key, string? Value)[]
        {
            (SettingsKeys.BrightDataToken, "bd-token"),
        });
        var accounts = new FakeIgAccountService(new[] { new IgAccount { Id = 1, Username = "buena", ProfileUrl = "https://www.instagram.com/buena/" } });
        var brightData = new FakeBrightDataService((_, _, _) =>
            new BrightDataFetchResult(new List<InstagramPost>(), new Dictionary<string, string>()));
        var recognition = new FakeRecognitionService();
        var activity = new FakeJobActivityHub();
        var jobRuns = new JobRunService(fixture.Db, activity);

        var job = new IgScrapeJob(jobRuns, settings, accounts, brightData, recognition, activity,
            NullLogger<IgScrapeJob>.Instance);
        await Assert.ThrowsAsync<InvalidOperationException>(() => job.ExecuteAsync(CancellationToken.None, "manual"));

        var run = await fixture.Db.JobRuns.SingleAsync();
        Assert.Equal("Failed", run.Status);
        Assert.Contains("DeepSeek", run.Error);
    }

    [Fact]
    public async Task MuxoJob_SyncsThenCrosschecks()
    {
        using var fixture = new TestDatabase();
        var settings = new FakeSettingsService(new (string Key, string? Value)[]
        {
            (SettingsKeys.DeepSeekApiKey, "sk-deepseek"),
        });
        var sync = new FakeMuxoSyncService((monthsAhead, _) => Task.FromResult(new MuxoSyncResponse
        {
            MuxoEventsScraped = 30,
            MuxoEventsNew = 5,
            MuxoEventsUpdated = 2
        }));
        var crossCheck = new FakeCrossCheckService((key, _) => Task.FromResult(new CrossCheckResponse
        {
            MuxoEventsConsidered = 30,
            OurEventsAnalyzed = 12,
            MatchesFound = 4
        }));
        var activity = new FakeJobActivityHub();
        var jobRuns = new JobRunService(fixture.Db, activity);

        var job = new MuxoJob(jobRuns, settings, sync, crossCheck, activity, NullLogger<MuxoJob>.Instance);
        await job.ExecuteAsync(CancellationToken.None, "recurring");

        Assert.Equal(2, sync.LastMonthsAhead);
        var run = await fixture.Db.JobRuns.SingleAsync();
        Assert.Equal("Succeeded", run.Status);
        Assert.Contains("\"muxoScraped\":30", run.SummaryJson);
        Assert.Contains("\"matches\":4", run.SummaryJson);
        Assert.Contains(activity.Entries, e => e.Message.Contains("Raspando muxojaleo.com"));
        Assert.Contains(activity.Entries, e => e.Message.Contains("30 eventos raspados"));
        Assert.Contains(activity.Entries, e => e.Message.Contains("✓ 4 cruces nuevos"));
    }

    [Fact]
    public async Task CleanupJob_CleansThePreviousMonthAndPurgesOldJobRuns()
    {
        using var fixture = new TestDatabase();
        var settings = new FakeSettingsService(new (string Key, string? Value)[]
        {
            (SettingsKeys.DeepSeekApiKey, "sk-deepseek"),
        });
        var cleanup = new FakeCleanupService((year, month, key, _) => Task.FromResult(new CleanupResponse
        {
            Month = $"{year:0000}-{month:00}",
            EventsAnalyzed = 10,
            DeletedCount = 3
        }));
        var activity = new FakeJobActivityHub();
        var jobRuns = new JobRunService(fixture.Db, activity);

        // A very old JobRun that the purge should delete.
        var oldRun = await jobRuns.StartAsync("Muxo", "recurring");
        oldRun.Status = "Succeeded";
        oldRun.CompletedAtUtc = DateTime.UtcNow.AddDays(-120);
        await fixture.Db.SaveChangesAsync();

        var job = new CleanupJob(jobRuns, settings, cleanup, activity, fixture.Db, NullLogger<CleanupJob>.Instance);
        await job.ExecuteAsync(CancellationToken.None, "manual");

        Assert.Contains(activity.Entries, e => e.Message.Contains("Analizando") && e.Message.Contains("duplicados"));
        Assert.Contains(activity.Entries, e => e.Message.Contains("✓ 10 eventos analizados, 3 duplicados eliminados"));

        var run = await fixture.Db.JobRuns.SingleAsync(r => r.JobName == "Cleanup");
        Assert.Equal("Succeeded", run.Status);
        Assert.Contains("\"deletedCount\":3", run.SummaryJson);

        // El job limpia el mes anterior al actual (mismo cálculo que usaba n8n).
        var expected = new DateTime(DateTime.UtcNow.Year, DateTime.UtcNow.Month, 1).AddMonths(-1);
        Assert.Contains($"\"month\":\"{expected:yyyy-MM}\"", run.SummaryJson);
        Assert.Equal(expected.Year, cleanup.LastYear);
        Assert.Equal(expected.Month, cleanup.LastMonth);

        // The 120-day-old run is gone.
        Assert.Empty(await fixture.Db.JobRuns.Where(r => r.JobName == "Muxo").ToListAsync());
    }
}
