using EventPipeline.Core.Data;
using EventPipeline.Core.Entities;
using EventPipeline.Core.Features.Muxo;
using EventPipeline.Core.Llm;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace EventPipeline.Tests;

public class CrossCheckServiceTests
{
    private static CrossCheckService CreateService(
        AppDbContext db,
        Func<List<CleanupEventItem>, List<MuxoEventItem>, List<CrossMatchResult>>? findCrossMatches = null)
        => new(
            new FakeDeepSeekService((_, _) => new List<PostAnalysisResult>(), findCrossMatches: findCrossMatches),
            db,
            NullLogger<CrossCheckService>.Instance);

    [Fact]
    public async Task CrossCheckAsync_PersistsValidMatches_OnePerEventAndMuxo()
    {
        using var fixture = new TestDatabase();
        fixture.Db.EventRecords.AddRange(
            TestData.CreateRecord("EVT-1", "Jueves Cong", postId: "p1"),
            TestData.CreateRecord("EVT-2", "Concierto X", postId: "p2"));
        fixture.Db.MuxoEvents.AddRange(
            TestData.CreateMuxoEvent("m1", "Jueves Cong", link: "https://www.instagram.com/p/X/"),
            TestData.CreateMuxoEvent("m2", "Otra cosa"));
        await fixture.Db.SaveChangesAsync();

        var service = CreateService(fixture.Db, (_, _) => new List<CrossMatchResult>
        {
            new() { EventUniqueId = "EVT-1", MuxoEventId = "m1", Reason = "Mismo evento" },
            new() { EventUniqueId = "EVT-2", MuxoEventId = "m1", Reason = "Uso duplicado del muxo" } // must be ignored: m1 already used
        });

        var response = await service.CrossCheckAsync("key");

        Assert.Equal(2, response.MuxoEventsConsidered);
        Assert.Equal(2, response.OurEventsAnalyzed);
        Assert.Equal(1, response.MatchesFound);
        var match = Assert.Single(response.Matches);
        Assert.Equal("EVT-1", match.EventUniqueId);
        Assert.Equal("Jueves Cong", match.MuxoTitle);
        Assert.Equal(1, await fixture.Db.CrossMatches.CountAsync());
    }

    [Fact]
    public async Task CrossCheckAsync_RemovesStaleMatches_ForDeletedEvents()
    {
        using var fixture = new TestDatabase();
        fixture.Db.EventRecords.Add(TestData.CreateRecord("EVT-ALIVE", "Vivo", postId: "p1"));
        var muxo = TestData.CreateMuxoEvent("m1", "Evento muxo");
        fixture.Db.MuxoEvents.Add(muxo);
        await fixture.Db.SaveChangesAsync();

        fixture.Db.CrossMatches.Add(new CrossMatch { EventUniqueId = "EVT-GONE", MuxoEventId = muxo.Id });
        await fixture.Db.SaveChangesAsync();

        var service = CreateService(fixture.Db, (_, _) => new List<CrossMatchResult>());

        var response = await service.CrossCheckAsync("key");

        Assert.Equal(0, response.MatchesFound);
        Assert.Equal(0, await fixture.Db.CrossMatches.CountAsync()); // stale match removed
    }

    [Fact]
    public async Task CrossCheckAsync_WithNoMuxoEvents_DoesNotCallTheLlm()
    {
        using var fixture = new TestDatabase();
        fixture.Db.EventRecords.Add(TestData.CreateRecord("EVT-1", "Evento", postId: "p1"));
        await fixture.Db.SaveChangesAsync();

        var service = CreateService(fixture.Db, (_, _) =>
            throw new InvalidOperationException("The LLM must not be called without muxo events"));

        var response = await service.CrossCheckAsync("key");

        Assert.Equal(0, response.MuxoEventsConsidered);
        Assert.Equal(0, response.MatchesFound);
    }
}
