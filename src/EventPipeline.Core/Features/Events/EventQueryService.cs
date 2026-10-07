using EventPipeline.Core.Contracts;
using EventPipeline.Core.Data;
using EventPipeline.Core.Entities;
using Microsoft.EntityFrameworkCore;

namespace EventPipeline.Core.Features.Events;

public interface IEventQueryService
{
    Task<EventDetailResponse?> GetByUniqueIdAsync(string eventUniqueId, CancellationToken ct = default);

    /// <summary>
    /// All events ordered by effective date (undated last); optionally filtered by
    /// occurrence range. Excluded events are hidden by default and only returned
    /// when <paramref name="includeExcluded"/> is true (the Events list uses that).
    /// </summary>
    Task<List<EventDetailResponse>> GetAllAsync(
        DateRange? dateRange = null,
        bool includeExcluded = false,
        CancellationToken ct = default);
}

public class EventQueryService : IEventQueryService
{
    private readonly AppDbContext _dbContext;

    public EventQueryService(AppDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<EventDetailResponse?> GetByUniqueIdAsync(string eventUniqueId, CancellationToken ct = default)
    {
        var record = await _dbContext.EventRecords
            .FirstOrDefaultAsync(e => e.EventUniqueId == eventUniqueId, ct);

        if (record == null)
            return null;

        var match = await _dbContext.CrossMatches
            .Include(m => m.MuxoEvent)
            .AsNoTracking()
            .FirstOrDefaultAsync(m => m.EventUniqueId == eventUniqueId, ct);

        return MapToDetailDto(record, match);
    }

    public async Task<List<EventDetailResponse>> GetAllAsync(
        DateRange? dateRange = null,
        bool includeExcluded = false,
        CancellationToken ct = default)
    {
        var records = await _dbContext.EventRecords
            .AsNoTracking()
            .Where(e => includeExcluded || !e.Excluded)
            .OrderBy(e => (e.EventDate ?? e.RecurrenceStartDate) == null)
            .ThenBy(e => e.EventDate ?? e.RecurrenceStartDate)
            .ThenBy(e => e.CreatedAt)
            .ToListAsync(ct);

        if (dateRange != null)
        {
            var from = dateRange.From ?? DateTime.MinValue;
            var to = dateRange.To ?? DateTime.MaxValue;
            records = records
                .Where(r => EventOccurrence.OccursInRange(r, from, to))
                .ToList();
        }

        var matches = await _dbContext.CrossMatches
            .Include(m => m.MuxoEvent)
            .AsNoTracking()
            .Where(m => records.Select(r => r.EventUniqueId).Contains(m.EventUniqueId))
            .ToDictionaryAsync(m => m.EventUniqueId, ct);

        return records.Select(r => MapToDetailDto(r, matches.GetValueOrDefault(r.EventUniqueId))).ToList();
    }

    private static EventDetailResponse MapToDetailDto(EventRecord e, CrossMatch? match = null)
    {
        return new EventDetailResponse
        {
            EventUniqueId = e.EventUniqueId,
            Title = e.Title,
            EventDate = e.EventDate,
            EventDateDescription = e.EventDateDescription,
            Summary = e.Summary,
            IsRecurrent = e.IsRecurrent,
            RecurrenceType = e.RecurrenceType,
            RecurrenceDaysOfWeek = e.RecurrenceDaysOfWeek,
            RecurrenceStartDate = e.RecurrenceStartDate,
            RecurrenceEndDate = e.RecurrenceEndDate,
            Account = e.Account,
            PostId = e.PostId,
            Caption = e.Caption,
            PostDatetime = e.PostDatetime,
            Url = e.Url,
            ImageUrl = e.ImageUrl,
            CreatedAt = e.CreatedAt,
            Excluded = e.Excluded,
            IsCrossed = match != null,
            MuxoTitle = match?.MuxoEvent?.Title,
            MuxoLink = match?.MuxoEvent?.Link,
            MuxoDate = match?.MuxoEvent?.Date
        };
    }
}
