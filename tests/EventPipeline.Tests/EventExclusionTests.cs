using EventPipeline.Core.Contracts;
using EventPipeline.Core.Entities;
using EventPipeline.Core.Features.Events;
using EventPipeline.Core.Features.Muxo;
using EventPipeline.Core.Features.Posts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace EventPipeline.Tests;

/// <summary>
/// Excluded events: hidden from the calendar queries, frozen against cleanup and
/// crosscheck, and never re-created when the same post is scraped again.
/// </summary>
public class EventExclusionTests
{
    private static EventRecord CreateExcludedRecord(string uniqueId, string postId, DateTime? eventDate = null)
        => TestData.CreateRecord(uniqueId, "Evento excluido", postId: postId, eventDate: eventDate)
            is var record ? (record.Excluded = true, record).Item2 : null!;

    [Fact]
    public async Task Query_HidesExcludedByDefaultAndShowsThemOnRequest()
    {
        using var fixture = new TestDatabase();
        fixture.Db.EventRecords.Add(TestData.CreateRecord("EVT-1", "Visible", postId: "p-1", eventDate: new DateTime(2026, 10, 8)));
        fixture.Db.EventRecords.Add(CreateExcludedRecord("EVT-2", "p-2", eventDate: new DateTime(2026, 10, 9)));
        await fixture.Db.SaveChangesAsync();

        var service = new EventQueryService(fixture.Db);

        var visible = await service.GetAllAsync();
        Assert.Single(visible);
        Assert.Equal("EVT-1", visible[0].EventUniqueId);

        var all = await service.GetAllAsync(includeExcluded: true);
        Assert.Equal(2, all.Count);
        Assert.True(all.Single(e => e.EventUniqueId == "EVT-2").Excluded);
    }

    [Fact]
    public async Task Crud_SetExcludedTogglesTheFlag()
    {
        using var fixture = new TestDatabase();
        fixture.Db.EventRecords.Add(TestData.CreateRecord("EVT-1", "Evento", postId: "p-1"));
        await fixture.Db.SaveChangesAsync();

        var query = new EventQueryService(fixture.Db);
        var crud = new EventCrudService(fixture.Db, query, NullLogger<EventCrudService>.Instance);

        var excluded = await crud.SetExcludedAsync("EVT-1", true);
        Assert.True(excluded!.Excluded);

        var included = await crud.SetExcludedAsync("EVT-1", false);
        Assert.False(included!.Excluded);
    }

    [Fact]
    public async Task Crud_UpdateKeepsExclusionWhenTheRequestDoesNotMentionIt()
    {
        using var fixture = new TestDatabase();
        fixture.Db.EventRecords.Add(CreateExcludedRecord("EVT-1", "p-1"));
        await fixture.Db.SaveChangesAsync();

        var query = new EventQueryService(fixture.Db);
        var crud = new EventCrudService(fixture.Db, query, NullLogger<EventCrudService>.Instance);

        // Sin Excluded en el request: se mantiene excluido.
        var updated = await crud.UpdateAsync("EVT-1", new UpdateEventRequest
        {
            Title = "Evento excluido (editado)",
            Summary = "Resumen"
        });
        Assert.True(updated!.Excluded);

        // Con Excluded=false: se reincluye.
        var reincluded = await crud.UpdateAsync("EVT-1", new UpdateEventRequest
        {
            Title = "Evento excluido (editado)",
            Summary = "Resumen",
            Excluded = false
        });
        Assert.False(reincluded!.Excluded);
    }

    [Fact]
    public async Task Cleanup_NeverAnalyzesOrDeletesExcludedEvents()
    {
        using var fixture = new TestDatabase();
        fixture.Db.EventRecords.Add(TestData.CreateRecord("EVT-VISIBLE", "Visible", postId: "p-1", eventDate: new DateTime(2026, 10, 8)));
        fixture.Db.EventRecords.Add(CreateExcludedRecord("EVT-EXCLUDED", "p-2", eventDate: new DateTime(2026, 10, 9)));
        await fixture.Db.SaveChangesAsync();

        List<CleanupEventItem>? sent = null;
        var deepSeek = new FakeDeepSeekService(
            analyze: (_, _) => new List<PostAnalysisResult>(),
            findDuplicates: (events, _) =>
            {
                sent = events;
                // El LLM "ve" un solo evento y no reporta duplicados.
                return new List<DuplicateGroupResult>();
            });
        var cleanup = new CleanupService(deepSeek, fixture.Db, NullLogger<CleanupService>.Instance);

        await cleanup.CleanupMonthAsync(2026, 10, "sk-test");

        Assert.NotNull(sent);
        Assert.Single(sent!); // solo el visible llega al LLM
        Assert.Equal("EVT-VISIBLE", sent![0].EventUniqueId);
        Assert.True(await fixture.Db.EventRecords.AnyAsync(e => e.EventUniqueId == "EVT-EXCLUDED"));
    }

    [Fact]
    public async Task CrossCheck_DoesNotSendExcludedEventsToTheLlm()
    {
        using var fixture = new TestDatabase();
        fixture.Db.EventRecords.Add(TestData.CreateRecord("EVT-VISIBLE", "Visible", postId: "p-1"));
        fixture.Db.EventRecords.Add(CreateExcludedRecord("EVT-EXCLUDED", "p-2"));
        fixture.Db.MuxoEvents.Add(TestData.CreateMuxoEvent("m-1", "Fiesta muxo"));
        await fixture.Db.SaveChangesAsync();

        List<CleanupEventItem>? ourEventsSent = null;
        var deepSeek = new FakeDeepSeekService(
            analyze: (_, _) => new List<PostAnalysisResult>(),
            findCrossMatches: (ourEvents, _) =>
            {
                ourEventsSent = ourEvents;
                return new List<CrossMatchResult>();
            });
        var crossCheck = new CrossCheckService(deepSeek, fixture.Db, NullLogger<CrossCheckService>.Instance);

        await crossCheck.CrossCheckAsync("sk-test");

        Assert.NotNull(ourEventsSent);
        Assert.Single(ourEventsSent!);
        Assert.Equal("EVT-VISIBLE", ourEventsSent![0].EventUniqueId);
    }

    [Fact]
    public async Task Recognition_DoesNotRecreateAnExcludedEventWhenItsPostIsScrapedAgain()
    {
        using var fixture = new TestDatabase();
        fixture.Db.EventRecords.Add(CreateExcludedRecord("EVT-1", "p-1"));
        await fixture.Db.SaveChangesAsync();

        var registry = new FakePostRegistryService();
        registry.Seed("https://instagram.com/p/p-1", TestData.EventResult(title: "Concierto repetido"));
        var deepSeek = new FakeDeepSeekService(analyze: (_, _) => new List<PostAnalysisResult>());
        var recognition = new RecognitionService(deepSeek, registry, fixture.Db, NullLogger<RecognitionService>.Instance);

        var posts = new List<InstagramPost> { TestData.CreatePost("p-1", caption: "Concierto") };
        var response = await recognition.RecognizeAsync(posts, "sk-test");

        // El post ya tiene un evento (excluido) en la BD: no se crea otro; el
        // "evento encontrado" de la respuesta es el ya conocido, que sigue excluido.
        Assert.Equal("EVT-1", response.Events.Single().EventUniqueId);
        Assert.Single(await fixture.Db.EventRecords.ToListAsync());
        Assert.True((await fixture.Db.EventRecords.SingleAsync()).Excluded);
    }
}
