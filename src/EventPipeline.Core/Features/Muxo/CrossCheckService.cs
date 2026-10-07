using EventPipeline.Core.Contracts;
using EventPipeline.Core.Data;
using EventPipeline.Core.Entities;
using EventPipeline.Core.Features.Events;
using EventPipeline.Core.Llm;
using Microsoft.EntityFrameworkCore;

namespace EventPipeline.Core.Features.Muxo;

public interface ICrossCheckService
{
    /// <summary>
    /// Asks the LLM to match our persisted events with the persisted muxojaleo.com
    /// events, and stores the new matches (1:1 per event and per muxo event).
    /// Does NOT scrape: the MuxoJob and the calendar page run the sync first.
    /// </summary>
    Task<CrossCheckResponse> CrossCheckAsync(string deepSeekApiKey, CancellationToken ct = default);
}

public class CrossCheckService : ICrossCheckService
{
    private readonly IDeepSeekService _deepSeekService;
    private readonly AppDbContext _dbContext;
    private readonly ILogger<CrossCheckService> _logger;

    public CrossCheckService(
        IDeepSeekService deepSeekService,
        AppDbContext dbContext,
        ILogger<CrossCheckService> logger)
    {
        _deepSeekService = deepSeekService;
        _dbContext = dbContext;
        _logger = logger;
    }

    public async Task<CrossCheckResponse> CrossCheckAsync(string deepSeekApiKey, CancellationToken ct = default)
    {
        // 1. Ask the LLM for matches between our events and the persisted muxo events.
        // Excluded events stay out of the crosscheck (they are out of the calendar).
        var ourEvents = await _dbContext.EventRecords.AsNoTracking()
            .Where(e => !e.Excluded)
            .ToListAsync(ct);
        var muxoEvents = await _dbContext.MuxoEvents.AsNoTracking().ToListAsync(ct);

        List<CrossMatchResult> matches = new();
        if (ourEvents.Count > 0 && muxoEvents.Count > 0)
        {
            var ourItems = ourEvents.Select(EventOccurrence.ToCleanupItem).ToList();
            var muxoItems = muxoEvents.Select(m => new MuxoEventItem
            {
                ExternalId = m.ExternalId,
                Title = m.Title,
                Date = m.Date,
                Venue = m.Venue,
                Categories = m.Categories,
                Link = m.Link
            }).ToList();

            matches = await _deepSeekService.FindCrossMatchesAsync(ourItems, muxoItems, deepSeekApiKey, ct);
        }

        // 2. Persist only new, valid matches (both ids must exist and the pair must not
        //    be persisted yet; each event and each muxo event matches at most once).
        //    Matches whose event was deleted since (e.g. by a cleanup) are stale: remove
        //    them so those muxo events can be matched again with the surviving event.
        var validOurIds = ourEvents.Select(e => e.EventUniqueId).ToHashSet();
        var muxoIdByExternalId = muxoEvents.ToDictionary(m => m.ExternalId);
        var existingMatches = await _dbContext.CrossMatches.ToListAsync(ct);

        var staleMatches = existingMatches.Where(m => !validOurIds.Contains(m.EventUniqueId)).ToList();
        if (staleMatches.Count > 0)
        {
            _dbContext.CrossMatches.RemoveRange(staleMatches);
            await _dbContext.SaveChangesAsync(ct);
            _logger.LogInformation("Crosscheck removed {Count} stale matches", staleMatches.Count);
        }

        var usedMuxoIds = existingMatches
            .Where(m => validOurIds.Contains(m.EventUniqueId))
            .Select(m => m.MuxoEventId)
            .ToHashSet();
        var usedEventIds = existingMatches
            .Where(m => validOurIds.Contains(m.EventUniqueId))
            .Select(m => m.EventUniqueId)
            .ToHashSet();

        var newMatches = new List<CrossMatch>();
        foreach (var pair in matches)
        {
            if (!validOurIds.Contains(pair.EventUniqueId) || usedEventIds.Contains(pair.EventUniqueId))
                continue;
            if (!muxoIdByExternalId.TryGetValue(pair.MuxoEventId, out var muxoEvent) ||
                usedMuxoIds.Contains(muxoEvent.Id))
                continue;

            newMatches.Add(new CrossMatch
            {
                EventUniqueId = pair.EventUniqueId,
                MuxoEventId = muxoEvent.Id,
                Reason = pair.Reason
            });
            usedEventIds.Add(pair.EventUniqueId);
            usedMuxoIds.Add(muxoEvent.Id);
        }

        if (newMatches.Count > 0)
        {
            _dbContext.CrossMatches.AddRange(newMatches);
            await _dbContext.SaveChangesAsync(ct);
            _logger.LogInformation("Crosscheck persisted {Count} new matches", newMatches.Count);
        }

        // 3. Build the response with the details of the new matches.
        var ourById = ourEvents.ToDictionary(e => e.EventUniqueId);
        var response = new CrossCheckResponse
        {
            MuxoEventsConsidered = muxoEvents.Count,
            OurEventsAnalyzed = ourEvents.Count,
            MatchesFound = newMatches.Count
        };

        foreach (var match in newMatches)
        {
            var muxoEvent = muxoIdByExternalId.Values.SingleOrDefault(m => m.Id == match.MuxoEventId);
            response.Matches.Add(new CrossMatchDto
            {
                EventUniqueId = match.EventUniqueId,
                EventTitle = ourById.GetValueOrDefault(match.EventUniqueId)?.Title,
                MuxoTitle = muxoEvent?.Title,
                MuxoDate = muxoEvent?.Date,
                MuxoLink = muxoEvent?.Link,
                Reason = match.Reason
            });
        }

        return response;
    }
}
