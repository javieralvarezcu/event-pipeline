using EventPipeline.Core.Data;
using EventPipeline.Core.Entities;
using EventPipeline.Core.Features.Posts;
using EventPipeline.Core.Llm;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace EventPipeline.Tests;

public class RecognitionServiceTests
{
    private static RecognitionService CreateService(
        AppDbContext db,
        Func<List<InstagramPost>, string, List<PostAnalysisResult>> analyze,
        Func<List<CleanupEventItem>, List<CleanupEventItem>, List<DuplicateGroupResult>>? findDuplicateCandidates = null,
        FakePostRegistryService? registry = null)
        => new(
            new FakeDeepSeekService(analyze, findDuplicateCandidates: findDuplicateCandidates),
            registry ?? new FakePostRegistryService(),
            db,
            NullLogger<RecognitionService>.Instance);

    [Fact]
    public async Task RecognizeAsync_PersistsOnlyEventPosts_AndReturnsAllPosts()
    {
        using var fixture = new TestDatabase();
        var posts = new List<InstagramPost>
        {
            TestData.CreatePost("p1", "cartel de concierto"),
            TestData.CreatePost("p2", "foto random")
        };
        var service = CreateService(fixture.Db, (_, _) => new List<PostAnalysisResult>
        {
            TestData.EventResult("Concierto X", "2026-09-19T22:00:00", "Sábado 19", "Resumen X"),
            TestData.NonEventResult()
        });

        var response = await service.RecognizeAsync(posts, "key");

        Assert.Equal(2, response.TotalPosts);
        Assert.Equal(1, response.EventsFound);
        var eventDto = response.Events[0];
        Assert.True(eventDto.IsEvent);
        Assert.Equal("Concierto X", eventDto.Title);
        Assert.Equal("p1", eventDto.PostId);
        Assert.False(response.Events[1].IsEvent);
        Assert.Equal(1, await fixture.Db.EventRecords.CountAsync());
    }

    [Fact]
    public async Task RecognizeAsync_WithKnownEventPost_SkipsTheLlm()
    {
        using var fixture = new TestDatabase();
        var posts = new List<InstagramPost> { TestData.CreatePost("p1", "cartel de concierto") };

        var registry = new FakePostRegistryService();
        registry.Seed(posts[0].Url,
            TestData.EventResult("Concierto X", "2026-09-19T22:00:00", "Sábado 19", "Resumen X"));

        var service = CreateService(fixture.Db,
            (_, _) => throw new InvalidOperationException("The LLM must not be called for known posts"),
            registry: registry);

        var response = await service.RecognizeAsync(posts, "key");

        Assert.Equal(1, response.EventsFound);
        Assert.Equal("Concierto X", response.Events[0].Title);
        Assert.Equal(1, await fixture.Db.EventRecords.CountAsync());
    }

    [Fact]
    public async Task RecognizeAsync_WithKnownNonEventPost_SkipsTheLlm()
    {
        using var fixture = new TestDatabase();
        var posts = new List<InstagramPost> { TestData.CreatePost("p1", "foto random") };

        var registry = new FakePostRegistryService();
        registry.Seed(posts[0].Url, TestData.NonEventResult());

        var service = CreateService(fixture.Db,
            (_, _) => throw new InvalidOperationException("The LLM must not be called for known posts"),
            registry: registry);

        var response = await service.RecognizeAsync(posts, "key");

        Assert.Equal(0, response.EventsFound);
        Assert.False(response.Events[0].IsEvent);
        Assert.Equal(0, await fixture.Db.EventRecords.CountAsync());
    }

    [Fact]
    public async Task RecognizeAsync_AnalyzesUnknownPosts_StoresThemAndReusesOnSecondRun()
    {
        using var fixture = new TestDatabase();
        var posts = new List<InstagramPost> { TestData.CreatePost("p1", "cartel de concierto") };

        var analyzeCalls = 0;
        var registry = new FakePostRegistryService();
        var service = CreateService(fixture.Db, (_, _) =>
        {
            analyzeCalls++;
            return new List<PostAnalysisResult>
            {
                TestData.EventResult("Concierto X", "2026-09-19T22:00:00", "Sábado 19", "Resumen X")
            };
        }, registry: registry);

        await service.RecognizeAsync(posts, "key");
        await service.RecognizeAsync(posts, "key");

        Assert.Equal(1, analyzeCalls);
        Assert.Equal(1, registry.StoreCallCount);
        Assert.Contains(posts[0].Url, registry.StoredUrls);
        Assert.Equal(1, await fixture.Db.EventRecords.CountAsync());
    }

    [Fact]
    public async Task RecognizeAsync_ConcurrentIdenticalRuns_ShareOneAnalysis()
    {
        DbContextOptions<AppDbContext>? options = null;
        using var fixture = new TestDatabase(o =>
        {
            options = o;
            return new AppDbContext(o);
        });
        using var db2 = new AppDbContext(options!);

        var posts = new List<InstagramPost> { TestData.CreatePost("p1", "cartel de concierto") };

        var analyzeCalls = 0;
        var registry = new FakePostRegistryService();
        Func<List<InstagramPost>, string, List<PostAnalysisResult>> analyze = (_, _) =>
        {
            Interlocked.Increment(ref analyzeCalls);
            Thread.Sleep(200); // let the other run queue on the analyze turn
            return new List<PostAnalysisResult>
            {
                TestData.EventResult("Concierto X", "2026-09-19T22:00:00", "Sábado 19", "Resumen X")
            };
        };

        var serviceA = CreateService(fixture.Db, analyze, registry: registry);
        var serviceB = CreateService(db2, analyze, registry: registry);

        var runA = serviceA.RecognizeAsync(posts, "key");
        var runB = serviceB.RecognizeAsync(posts, "key");
        await Task.WhenAll(runA, runB);

        Assert.Equal(1, analyzeCalls);
        Assert.Equal(1, await fixture.Db.EventRecords.CountAsync());
    }

    [Fact]
    public async Task RecognizeAsync_DropsCandidateReportedAsDuplicate_AndReturnsExistingRecord()
    {
        using var fixture = new TestDatabase();
        var existing = TestData.CreateRecord("EVT-OLD", "Jueves Cong", postId: "p-old",
            eventDate: new DateTime(2026, 9, 24));
        fixture.Db.EventRecords.Add(existing);
        await fixture.Db.SaveChangesAsync();

        var posts = new List<InstagramPost> { TestData.CreatePost("p-new", "Jueves Cong esta semana") };
        var service = CreateService(fixture.Db, (_, _) => new List<PostAnalysisResult>
        {
            TestData.EventResult("Jueves Cong", "2026-09-24T23:00:00")
        }, findDuplicateCandidates: (candidates, existingEvents) => new List<DuplicateGroupResult>
        {
            new()
            {
                KeepEventId = "EVT-OLD",
                DuplicateEventIds = new List<string> { candidates[0].EventUniqueId },
                Reason = "Misma fiesta semanal"
            }
        });

        var response = await service.RecognizeAsync(posts, "key");

        Assert.Equal(1, response.EventsFound);
        Assert.Equal("EVT-OLD", response.Events[0].EventUniqueId);
        Assert.Equal(1, await fixture.Db.EventRecords.CountAsync()); // the duplicate was not saved
    }

    [Fact]
    public async Task RecognizeAsync_WithTwoNewPostsOfSameEvent_KeepsOnlyKeeper()
    {
        using var fixture = new TestDatabase();
        var posts = new List<InstagramPost>
        {
            TestData.CreatePost("p1", "Jueves Cong"),
            TestData.CreatePost("p2", "Jueves Cong otra vez")
        };

        var service = CreateService(fixture.Db, (_, _) => new List<PostAnalysisResult>
        {
            TestData.EventResult("Jueves Cong"),
            TestData.EventResult("Jueves Cong")
        }, findDuplicateCandidates: (candidates, _) => new List<DuplicateGroupResult>
        {
            new()
            {
                KeepEventId = candidates[0].EventUniqueId,
                DuplicateEventIds = new List<string> { candidates[1].EventUniqueId },
                Reason = "Mismo evento"
            }
        });

        var response = await service.RecognizeAsync(posts, "key");

        Assert.Equal(1, await fixture.Db.EventRecords.CountAsync());
        Assert.All(response.Events, e => Assert.Equal(response.Events[0].EventUniqueId, e.EventUniqueId));
    }

    [Fact]
    public async Task RecognizeAsync_WithPostIdAlreadyInDb_ReturnsDbRecordWithoutCallingDedupLlm()
    {
        using var fixture = new TestDatabase();
        var existing = TestData.CreateRecord("EVT-OLD", "Concierto persistido", postId: "p1");
        fixture.Db.EventRecords.Add(existing);
        await fixture.Db.SaveChangesAsync();

        var posts = new List<InstagramPost> { TestData.CreatePost("p1", "Concierto persistido") };
        var service = CreateService(fixture.Db, (_, _) => new List<PostAnalysisResult>
        {
            TestData.EventResult("Concierto persistido")
        }, findDuplicateCandidates: (_, _) =>
            throw new InvalidOperationException("Dedup LLM must not be called for already persisted posts"));

        var response = await service.RecognizeAsync(posts, "key");

        Assert.Equal("EVT-OLD", response.Events[0].EventUniqueId);
        Assert.Equal(1, await fixture.Db.EventRecords.CountAsync());
    }
}
