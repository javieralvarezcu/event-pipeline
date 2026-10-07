using EventPipeline.Core.Contracts;
using EventPipeline.Core.Data;
using EventPipeline.Core.Entities;
using Microsoft.EntityFrameworkCore;

namespace EventPipeline.Core.Features.Muxo;

public interface IMuxoSyncService
{
    /// <summary>
    /// Scrapes the muxojaleo.com calendar and upserts the events into the database
    /// (dedupe by external id, updating fields the site changed). No LLM involved.
    /// </summary>
    Task<MuxoSyncResponse> SyncAsync(int monthsAhead = 2, CancellationToken ct = default);
}

public class MuxoSyncService : IMuxoSyncService
{
    private readonly IMuxoScraperService _muxoScraperService;
    private readonly AppDbContext _dbContext;
    private readonly ILogger<MuxoSyncService> _logger;

    public MuxoSyncService(
        IMuxoScraperService muxoScraperService,
        AppDbContext dbContext,
        ILogger<MuxoSyncService> logger)
    {
        _muxoScraperService = muxoScraperService;
        _dbContext = dbContext;
        _logger = logger;
    }

    public async Task<MuxoSyncResponse> SyncAsync(int monthsAhead = 2, CancellationToken ct = default)
    {
        var scraped = await _muxoScraperService.ScrapeUpcomingAsync(monthsAhead, ct);

        var externalIds = scraped.Select(e => e.ExternalId).ToList();
        var storedByExternalId = await _dbContext.MuxoEvents
            .Where(m => externalIds.Contains(m.ExternalId))
            .ToDictionaryAsync(m => m.ExternalId, ct);

        var newMuxoEvents = new List<MuxoEvent>();
        var updatedCount = 0;
        foreach (var scrapedEvent in scraped)
        {
            if (storedByExternalId.TryGetValue(scrapedEvent.ExternalId, out var stored))
            {
                if (stored.Title != scrapedEvent.Title || stored.Date != scrapedEvent.Date ||
                    stored.Venue != scrapedEvent.Venue || stored.Link != scrapedEvent.Link ||
                    stored.Categories != scrapedEvent.Categories || stored.Price != scrapedEvent.Price)
                {
                    stored.Title = scrapedEvent.Title;
                    stored.Date = scrapedEvent.Date;
                    stored.Venue = scrapedEvent.Venue;
                    stored.Link = scrapedEvent.Link;
                    stored.Categories = scrapedEvent.Categories;
                    stored.Price = scrapedEvent.Price;
                    updatedCount++;
                }
            }
            else
            {
                newMuxoEvents.Add(scrapedEvent);
            }
        }

        if (newMuxoEvents.Count > 0)
            _dbContext.MuxoEvents.AddRange(newMuxoEvents);

        if (newMuxoEvents.Count > 0 || updatedCount > 0)
        {
            await _dbContext.SaveChangesAsync(ct);
            _logger.LogInformation(
                "Muxo sync: {Scraped} scraped, {New} new, {Updated} updated",
                scraped.Count, newMuxoEvents.Count, updatedCount);
        }

        return new MuxoSyncResponse
        {
            MuxoEventsScraped = scraped.Count,
            MuxoEventsNew = newMuxoEvents.Count,
            MuxoEventsUpdated = updatedCount
        };
    }
}
