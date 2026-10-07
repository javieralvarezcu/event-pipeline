using EventPipeline.Web.Features.Jobs;
using Microsoft.EntityFrameworkCore;

namespace EventPipeline.Tests;

public class JobRunServiceTests
{
    [Fact]
    public async Task StartCompleteFail_RecordsTheFullCycle()
    {
        using var fixture = new TestDatabase();
        var activity = new FakeJobActivityHub();
        var service = new JobRunService(fixture.Db, activity);

        var succeeded = await service.StartAsync("Muxo", "manual");
        await service.CompleteAsync(succeeded.Id, new { muxoScraped = 12, matches = 3 });

        var failed = await service.StartAsync("IgScrape", "recurring");
        await service.FailAsync(failed.Id, new InvalidOperationException("Configura la API key de DeepSeek en Ajustes."));

        var runs = await service.GetLatestAsync(10);
        Assert.Equal(2, runs.Count);
        Assert.Equal("IgScrape", runs[0].JobName); // newest first
        Assert.Equal("recurring", runs[0].TriggerType);
        Assert.Equal("Failed", runs[0].Status);
        Assert.NotNull(runs[0].CompletedAtUtc);
        Assert.Contains("DeepSeek", runs[0].Error);
        Assert.Null(runs[0].SummaryJson);

        Assert.Equal("Succeeded", runs[1].Status);
        Assert.Contains("\"muxoScraped\":12", runs[1].SummaryJson);
        Assert.Contains("\"matches\":3", runs[1].SummaryJson);

        // Los cambios de estado se emiten al panel en vivo (Running → terminal).
        Assert.Equal(new[] { "Running", "Succeeded", "Running", "Failed" }, activity.RunEvents.Select(e => e.Status));
    }

    [Fact]
    public async Task GetLatest_FiltersByJobName()
    {
        using var fixture = new TestDatabase();
        var activity = new FakeJobActivityHub();
        var service = new JobRunService(fixture.Db, activity);

        var muxo = await service.StartAsync("Muxo", "manual");
        await service.CompleteAsync(muxo.Id, new { ok = true });
        var ig = await service.StartAsync("IgScrape", "manual");
        await service.CompleteAsync(ig.Id, new { ok = true });

        var muxoRuns = await service.GetLatestAsync(10, jobName: "Muxo");
        Assert.Single(muxoRuns);
        Assert.Equal("Muxo", muxoRuns[0].JobName);

        Assert.Empty(await service.GetLatestAsync(10, jobName: "Cleanup"));
    }

    [Fact]
    public async Task Complete_TruncatesNothingOnShortErrors()
    {
        using var fixture = new TestDatabase();
        var activity = new FakeJobActivityHub();
        var service = new JobRunService(fixture.Db, activity);

        var run = await service.StartAsync("Cleanup", "manual");
        await service.FailAsync(run.Id, new Exception(new string('x', 5000)));

        var stored = await fixture.Db.JobRuns.SingleAsync(r => r.Id == run.Id);
        Assert.Equal(4000, stored.Error!.Length);
    }
}
