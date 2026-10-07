using EventPipeline.Core.Contracts;
using EventPipeline.Core.Data;
using EventPipeline.Core.Entities;
using EventPipeline.Core.Features.Events;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace EventPipeline.Tests;

public class EventCrudQueryTests
{
    private static (EventQueryService Query, EventCrudService Crud) CreateServices(AppDbContext db)
    {
        var query = new EventQueryService(db);
        var crud = new EventCrudService(db, query, NullLogger<EventCrudService>.Instance);
        return (query, crud);
    }

    [Fact]
    public async Task GetAllAsync_OrdersByEffectiveDate_UndatedLast()
    {
        using var fixture = new TestDatabase();
        fixture.Db.EventRecords.AddRange(
            TestData.CreateRecord("EVT-1", "Con fecha", postId: "p1", eventDate: new DateTime(2026, 10, 10)),
            TestData.CreateRecord("EVT-2", "Sin fecha", postId: "p2"),
            TestData.CreateRecord("EVT-3", "Antes", postId: "p3", eventDate: new DateTime(2026, 9, 1)));
        await fixture.Db.SaveChangesAsync();

        var (query, _) = CreateServices(fixture.Db);
        var events = await query.GetAllAsync();

        Assert.Equal(new[] { "EVT-3", "EVT-1", "EVT-2" }, events.Select(e => e.EventUniqueId));
    }

    [Fact]
    public async Task GetAllAsync_WithRange_FiltersByOccurrence()
    {
        using var fixture = new TestDatabase();
        fixture.Db.EventRecords.AddRange(
            TestData.CreateRecord("EVT-IN", "Dentro", postId: "p1", eventDate: new DateTime(2026, 9, 15)),
            TestData.CreateRecord("EVT-OUT", "Fuera", postId: "p2", eventDate: new DateTime(2026, 11, 1)));
        await fixture.Db.SaveChangesAsync();

        var (query, _) = CreateServices(fixture.Db);
        var events = await query.GetAllAsync(
            new DateRange(new DateTime(2026, 9, 1), new DateTime(2026, 9, 30)));

        var single = Assert.Single(events);
        Assert.Equal("EVT-IN", single.EventUniqueId);
    }

    [Fact]
    public async Task GetByUniqueIdAsync_IncludesTheCrossMatch()
    {
        using var fixture = new TestDatabase();
        fixture.Db.EventRecords.Add(TestData.CreateRecord("EVT-1", "Concierto", postId: "p1"));
        var muxo = TestData.CreateMuxoEvent("m1", "Concierto en muxo", date: new DateTime(2026, 9, 19));
        fixture.Db.MuxoEvents.Add(muxo);
        await fixture.Db.SaveChangesAsync();
        fixture.Db.CrossMatches.Add(new CrossMatch { EventUniqueId = "EVT-1", MuxoEventId = muxo.Id });
        await fixture.Db.SaveChangesAsync();

        var (query, _) = CreateServices(fixture.Db);
        var result = await query.GetByUniqueIdAsync("EVT-1");

        Assert.NotNull(result);
        Assert.True(result.IsCrossed);
        Assert.Equal("Concierto en muxo", result.MuxoTitle);
        Assert.Equal("m1", await fixture.Db.CrossMatches.Select(m => m.MuxoEvent.ExternalId).SingleAsync());
    }

    [Fact]
    public async Task UpdateAsync_EditsFields_AndNullKeepsUrl()
    {
        using var fixture = new TestDatabase();
        fixture.Db.EventRecords.Add(TestData.CreateRecord("EVT-1", "Viejo título", postId: "p1"));
        await fixture.Db.SaveChangesAsync();

        var (_, crud) = CreateServices(fixture.Db);
        var result = await crud.UpdateAsync("EVT-1", new UpdateEventRequest
        {
            Title = "Título nuevo",
            Summary = "Resumen nuevo",
            EventDate = new DateTime(2026, 10, 20)
        });

        Assert.NotNull(result);
        Assert.Equal("Título nuevo", result.Title);
        var stored = await fixture.Db.EventRecords.SingleAsync();
        Assert.Equal("Título nuevo", stored.Title);
        Assert.Equal("https://instagram.com/p/p1", stored.Url); // null Url keeps the current value
        Assert.Equal(new DateTime(2026, 10, 20), stored.EventDate);
    }

    [Fact]
    public async Task UpdateAsync_WithUnknownId_ReturnsNull()
    {
        using var fixture = new TestDatabase();
        var (_, crud) = CreateServices(fixture.Db);

        var result = await crud.UpdateAsync("EVT-NOPE", new UpdateEventRequest
        {
            Title = "X",
            Summary = "Y"
        });

        Assert.Null(result);
    }

    [Fact]
    public async Task DeleteAsync_RemovesTheEvent_AndItsCrossMatches()
    {
        using var fixture = new TestDatabase();
        fixture.Db.EventRecords.Add(TestData.CreateRecord("EVT-1", "Evento", postId: "p1"));
        var muxo = TestData.CreateMuxoEvent("m1", "Muxo");
        fixture.Db.MuxoEvents.Add(muxo);
        await fixture.Db.SaveChangesAsync();
        fixture.Db.CrossMatches.Add(new CrossMatch { EventUniqueId = "EVT-1", MuxoEventId = muxo.Id });
        await fixture.Db.SaveChangesAsync();

        var (_, crud) = CreateServices(fixture.Db);
        var deleted = await crud.DeleteAsync("EVT-1");

        Assert.True(deleted);
        Assert.Equal(0, await fixture.Db.EventRecords.CountAsync());
        Assert.Equal(0, await fixture.Db.CrossMatches.CountAsync());
        Assert.Equal(1, await fixture.Db.MuxoEvents.CountAsync()); // the muxo event survives
    }

    [Fact]
    public async Task DeleteAsync_WithUnknownId_ReturnsFalse()
    {
        using var fixture = new TestDatabase();
        var (_, crud) = CreateServices(fixture.Db);

        var deleted = await crud.DeleteAsync("EVT-NOPE");

        Assert.False(deleted);
    }
}
