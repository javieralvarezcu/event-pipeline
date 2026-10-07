using EventPipeline.Core.Data;
using EventPipeline.Core.Entities;
using EventPipeline.Core.Features.Muxo;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace EventPipeline.Tests;

public class MuxoSyncServiceTests
{
    private static MuxoSyncService CreateService(AppDbContext db, Func<int, List<MuxoEvent>> scrape)
        => new(
            new FakeMuxoScraperService(scrape),
            db,
            NullLogger<MuxoSyncService>.Instance);

    [Fact]
    public async Task SyncAsync_InsertsNewEvents_AndUpdatesChangedOnes()
    {
        using var fixture = new TestDatabase();
        fixture.Db.MuxoEvents.Add(new MuxoEvent
        {
            ExternalId = "existing-1",
            Title = "Título viejo",
            Date = new DateTime(2026, 10, 1),
            Venue = "Sitio viejo"
        });
        await fixture.Db.SaveChangesAsync();

        var service = CreateService(fixture.Db, _ => new List<MuxoEvent>
        {
            new() { ExternalId = "existing-1", Title = "Título nuevo", Date = new DateTime(2026, 10, 1), Venue = "Sitio nuevo" },
            new() { ExternalId = "new-2", Title = "Evento nuevo", Date = new DateTime(2026, 10, 5) }
        });

        var response = await service.SyncAsync();

        Assert.Equal(2, response.MuxoEventsScraped);
        Assert.Equal(1, response.MuxoEventsNew);
        Assert.Equal(1, response.MuxoEventsUpdated);

        var stored = await fixture.Db.MuxoEvents.OrderBy(m => m.ExternalId).ToListAsync();
        Assert.Equal(2, stored.Count);
        Assert.Equal("Título nuevo", stored[0].Title);
        Assert.Equal("Sitio nuevo", stored[0].Venue);
        Assert.Equal("Evento nuevo", stored[1].Title);
    }

    [Fact]
    public async Task SyncAsync_WithUnchangedEvents_ReportsNoChanges()
    {
        using var fixture = new TestDatabase();
        fixture.Db.MuxoEvents.Add(new MuxoEvent
        {
            ExternalId = "stable-1",
            Title = "Estable",
            Date = new DateTime(2026, 10, 1),
            Venue = "Sitio"
        });
        await fixture.Db.SaveChangesAsync();

        var service = CreateService(fixture.Db, _ => new List<MuxoEvent>
        {
            new() { ExternalId = "stable-1", Title = "Estable", Date = new DateTime(2026, 10, 1), Venue = "Sitio" }
        });

        var response = await service.SyncAsync();

        Assert.Equal(0, response.MuxoEventsNew);
        Assert.Equal(0, response.MuxoEventsUpdated);
        Assert.Equal(1, await fixture.Db.MuxoEvents.CountAsync());
    }

    [Fact]
    public async Task SyncAsync_PassesTheRequestedMonthsToTheScraper()
    {
        using var fixture = new TestDatabase();
        var scraper = new FakeMuxoScraperService(_ => new List<MuxoEvent>());
        var service = new MuxoSyncService(scraper, fixture.Db, NullLogger<MuxoSyncService>.Instance);

        await service.SyncAsync(monthsAhead: 3);

        Assert.Equal(3, scraper.LastMonthsAhead);
    }
}
