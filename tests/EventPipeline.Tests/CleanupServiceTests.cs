using EventPipeline.Core.Data;
using EventPipeline.Core.Entities;
using EventPipeline.Core.Features.Events;
using EventPipeline.Core.Llm;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace EventPipeline.Tests;

public class CleanupServiceTests
{
    private static CleanupService CreateService(
        AppDbContext db,
        Func<List<CleanupEventItem>, string, List<DuplicateGroupResult>>? findDuplicates = null)
        => new(
            new FakeDeepSeekService((_, _) => new List<PostAnalysisResult>(), findDuplicates),
            db,
            NullLogger<CleanupService>.Instance);

    [Fact]
    public async Task CleanupMonthAsync_DeletesOnlyReportedDuplicates_AndProtectsKeepers()
    {
        using var fixture = new TestDatabase();
        fixture.Db.EventRecords.AddRange(
            TestData.CreateRecord("EVT-KEEP", "Jueves Cong", postId: "p1", eventDate: new DateTime(2026, 9, 24)),
            TestData.CreateRecord("EVT-DUP", "Jueves de fiesta", postId: "p2", eventDate: new DateTime(2026, 9, 25)),
            TestData.CreateRecord("EVT-OTHER", "Otro concierto", postId: "p3", eventDate: new DateTime(2026, 10, 2)));
        await fixture.Db.SaveChangesAsync();

        var service = CreateService(fixture.Db, (events, monthLabel) =>
        {
            Assert.Equal("2026-09", monthLabel);
            // The undated events are also sent, but there are none here.
            Assert.Equal(2, events.Count);
            return new List<DuplicateGroupResult>
            {
                new()
                {
                    KeepEventId = "EVT-KEEP",
                    DuplicateEventIds = new List<string> { "EVT-DUP" },
                    Reason = "Misma fiesta semanal"
                }
            };
        });

        var response = await service.CleanupMonthAsync(2026, 9, "key");

        Assert.Equal(2, response.EventsAnalyzed);
        Assert.Equal(1, response.DeletedCount);
        var group = Assert.Single(response.Groups);
        Assert.Equal("EVT-KEEP", group.KeepEventId);
        Assert.Equal("EVT-DUP", Assert.Single(group.Removed).EventUniqueId);

        var remaining = await fixture.Db.EventRecords.Select(e => e.EventUniqueId).ToListAsync();
        Assert.DoesNotContain("EVT-DUP", remaining);
        Assert.Contains("EVT-KEEP", remaining);
        Assert.Contains("EVT-OTHER", remaining);
    }

    [Fact]
    public async Task CleanupMonthAsync_AlsoSendsUndatedEvents_AndDeletesTheirCrossMatches()
    {
        using var fixture = new TestDatabase();
        fixture.Db.EventRecords.AddRange(
            TestData.CreateRecord("EVT-DATED", "Jueves Cong", postId: "p1", eventDate: new DateTime(2026, 9, 24)),
            TestData.CreateRecord("EVT-UNDATED", "Jueves Cong sin fecha", postId: "p2"));
        var muxo = TestData.CreateMuxoEvent("m1", "Jueves Cong en muxo");
        fixture.Db.MuxoEvents.Add(muxo);
        await fixture.Db.SaveChangesAsync();

        var match = new CrossMatch { EventUniqueId = "EVT-UNDATED", MuxoEventId = muxo.Id };
        fixture.Db.CrossMatches.Add(match);
        await fixture.Db.SaveChangesAsync();

        var service = CreateService(fixture.Db, (events, _) =>
        {
            Assert.Equal(2, events.Count); // month event + undated event
            return new List<DuplicateGroupResult>
            {
                new()
                {
                    KeepEventId = "EVT-DATED",
                    DuplicateEventIds = new List<string> { "EVT-UNDATED" },
                    Reason = "Sin fecha duplicado del fechado"
                }
            };
        });

        var response = await service.CleanupMonthAsync(2026, 9, "key");

        Assert.Equal(1, response.DeletedCount);
        Assert.Equal(0, await fixture.Db.CrossMatches.CountAsync()); // stale match removed
    }

    [Fact]
    public async Task CleanupMonthAsync_IgnoresGroupsWithUnknownKeepers()
    {
        using var fixture = new TestDatabase();
        fixture.Db.EventRecords.AddRange(
            TestData.CreateRecord("EVT-A", "Evento A", postId: "p1", eventDate: new DateTime(2026, 9, 10)),
            TestData.CreateRecord("EVT-B", "Evento B", postId: "p2", eventDate: new DateTime(2026, 9, 11)));
        await fixture.Db.SaveChangesAsync();

        var service = CreateService(fixture.Db, (_, _) => new List<DuplicateGroupResult>
        {
            new()
            {
                KeepEventId = "EVT-UNKNOWN",
                DuplicateEventIds = new List<string> { "EVT-A", "EVT-B" },
                Reason = "Keeper inventado"
            }
        });

        var response = await service.CleanupMonthAsync(2026, 9, "key");

        Assert.Equal(0, response.DeletedCount);
        Assert.Equal(2, await fixture.Db.EventRecords.CountAsync()); // nothing deleted
    }

    [Fact]
    public async Task CleanupMonthAsync_WithNoEvents_DoesNotCallTheLlm()
    {
        using var fixture = new TestDatabase();
        var service = CreateService(fixture.Db, (_, _) =>
            throw new InvalidOperationException("The LLM must not be called with nothing to analyze"));

        var response = await service.CleanupMonthAsync(2026, 9, "key");

        Assert.Equal("2026-09", response.Month);
        Assert.Equal(0, response.EventsAnalyzed);
    }
}
