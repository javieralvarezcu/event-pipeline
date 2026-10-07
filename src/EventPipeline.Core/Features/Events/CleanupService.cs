using EventPipeline.Core.Contracts;
using EventPipeline.Core.Data;
using EventPipeline.Core.Llm;
using Microsoft.EntityFrameworkCore;

namespace EventPipeline.Core.Features.Events;

public interface ICleanupService
{
    /// <summary>
    /// Asks the LLM which persisted events of the given month (plus the undated
    /// ones) are duplicates of the same real event, and deletes the duplicates.
    /// </summary>
    Task<CleanupResponse> CleanupMonthAsync(
        int year,
        int month,
        string deepSeekApiKey,
        CancellationToken ct = default);
}

public class CleanupService : ICleanupService
{
    private readonly IDeepSeekService _deepSeekService;
    private readonly AppDbContext _dbContext;
    private readonly ILogger<CleanupService> _logger;

    public CleanupService(
        IDeepSeekService deepSeekService,
        AppDbContext dbContext,
        ILogger<CleanupService> logger)
    {
        _deepSeekService = deepSeekService;
        _dbContext = dbContext;
        _logger = logger;
    }

    public async Task<CleanupResponse> CleanupMonthAsync(
        int year,
        int month,
        string deepSeekApiKey,
        CancellationToken ct = default)
    {
        var monthLabel = $"{year:0000}-{month:00}";
        var monthStart = new DateTime(year, month, 1);
        var monthEnd = monthStart.AddMonths(1).AddDays(-1);

        var records = await _dbContext.EventRecords.AsNoTracking().ToListAsync(ct);

        // Analyze: events occurring within the month + events without any computable
        // date (those in the "sin fecha" list, crossed against the month's events).
        // Excluded events are frozen: never sent to the LLM, never deleted (their
        // PostId must stay occupied so re-scrapes don't bring them back).
        var analyzed = records
            .Where(r => !r.Excluded)
            .Where(r => EventOccurrence.OccursInRange(r, monthStart, monthEnd) || EventOccurrence.HasNoDate(r))
            .ToList();

        if (analyzed.Count == 0)
            return new CleanupResponse { Month = monthLabel };

        var items = analyzed.Select(EventOccurrence.ToCleanupItem).ToList();
        var groups = await _deepSeekService.FindDuplicateEventsAsync(items, monthLabel, deepSeekApiKey, ct);

        // Only ids that were sent to the LLM can be deleted, and an id chosen as the
        // keeper of any group is protected. Groups whose keeper is unknown are skipped
        // entirely so a bogus keeper can't cause a whole group to be deleted.
        var analyzedIds = analyzed.Select(r => r.EventUniqueId).ToHashSet();
        var recordsById = analyzed.ToDictionary(r => r.EventUniqueId);
        var keepIds = groups
            .Where(g => analyzedIds.Contains(g.KeepEventId))
            .Select(g => g.KeepEventId)
            .ToHashSet();
        var toDelete = groups
            .Where(g => analyzedIds.Contains(g.KeepEventId))
            .SelectMany(g => g.DuplicateEventIds)
            .Where(analyzedIds.Contains)
            .Where(id => !keepIds.Contains(id))
            .Distinct()
            .ToHashSet();

        if (toDelete.Count > 0)
        {
            var entities = await _dbContext.EventRecords
                .Where(e => toDelete.Contains(e.EventUniqueId))
                .ToListAsync(ct);
            _dbContext.EventRecords.RemoveRange(entities);

            // Cross-matches referencing the removed events would become stale: delete
            // them too, so those muxo events can be matched again on the next crosscheck.
            var deletedIds = entities.Select(e => e.EventUniqueId).ToList();
            var staleMatches = await _dbContext.CrossMatches
                .Where(m => deletedIds.Contains(m.EventUniqueId))
                .ToListAsync(ct);
            _dbContext.CrossMatches.RemoveRange(staleMatches);

            await _dbContext.SaveChangesAsync(ct);
            _logger.LogInformation(
                "Cleanup removed {Count} duplicate events for month {Month}", entities.Count, monthLabel);
        }

        var response = new CleanupResponse { Month = monthLabel, EventsAnalyzed = analyzed.Count };
        foreach (var group in groups)
        {
            if (!analyzedIds.Contains(group.KeepEventId))
                continue;

            var removed = group.DuplicateEventIds
                .Where(toDelete.Contains)
                .Select(id => recordsById[id])
                .ToList();
            if (removed.Count == 0)
                continue;

            response.Groups.Add(new CleanupGroupDto
            {
                KeepEventId = group.KeepEventId,
                KeepTitle = recordsById[group.KeepEventId].Title,
                Removed = removed
                    .Select(r => new CleanupRemovedDto
                    {
                        EventUniqueId = r.EventUniqueId,
                        Title = r.Title,
                        Account = r.Account
                    })
                    .ToList(),
                Reason = group.Reason
            });
        }

        response.DeletedCount = response.Groups.Sum(g => g.Removed.Count);
        return response;
    }
}
