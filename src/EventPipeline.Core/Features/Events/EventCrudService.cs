using EventPipeline.Core.Contracts;
using EventPipeline.Core.Data;
using Microsoft.EntityFrameworkCore;

namespace EventPipeline.Core.Features.Events;

public interface IEventCrudService
{
    Task<EventDetailResponse?> UpdateAsync(
        string eventUniqueId,
        UpdateEventRequest request,
        CancellationToken ct = default);

    /// <summary>Excludes/re-includes an event in the calendar. Returns the updated event or null.</summary>
    Task<EventDetailResponse?> SetExcludedAsync(string eventUniqueId, bool excluded, CancellationToken ct = default);

    Task<bool> DeleteAsync(string eventUniqueId, CancellationToken ct = default);
}

public class EventCrudService : IEventCrudService
{
    private readonly AppDbContext _dbContext;
    private readonly IEventQueryService _eventQueryService;
    private readonly ILogger<EventCrudService> _logger;

    public EventCrudService(
        AppDbContext dbContext,
        IEventQueryService eventQueryService,
        ILogger<EventCrudService> logger)
    {
        _dbContext = dbContext;
        _eventQueryService = eventQueryService;
        _logger = logger;
    }

    public async Task<EventDetailResponse?> UpdateAsync(
        string eventUniqueId,
        UpdateEventRequest request,
        CancellationToken ct = default)
    {
        var record = await _dbContext.EventRecords
            .FirstOrDefaultAsync(e => e.EventUniqueId == eventUniqueId, ct);
        if (record == null)
            return null;

        record.Title = request.Title.Trim();
        record.Summary = request.Summary.Trim();
        record.EventDate = request.EventDate;
        record.EventDateDescription = NullIfWhitespace(request.EventDateDescription);
        record.IsRecurrent = request.IsRecurrent;
        record.RecurrenceType = NullIfWhitespace(request.RecurrenceType);
        record.RecurrenceDaysOfWeek = NullIfWhitespace(request.RecurrenceDaysOfWeek);
        record.RecurrenceStartDate = request.RecurrenceStartDate;
        record.RecurrenceEndDate = request.RecurrenceEndDate;
        record.Url = NullIfWhitespace(request.Url) ?? record.Url;
        record.ImageUrl = NullIfWhitespace(request.ImageUrl);
        record.Caption = request.Caption ?? string.Empty;
        if (request.Excluded.HasValue)
            record.Excluded = request.Excluded.Value;

        await _dbContext.SaveChangesAsync(ct);
        _logger.LogInformation("Updated event {EventUniqueId}", eventUniqueId);

        return await _eventQueryService.GetByUniqueIdAsync(eventUniqueId, ct);
    }

    public async Task<EventDetailResponse?> SetExcludedAsync(
        string eventUniqueId, bool excluded, CancellationToken ct = default)
    {
        var record = await _dbContext.EventRecords
            .FirstOrDefaultAsync(e => e.EventUniqueId == eventUniqueId, ct);
        if (record == null)
            return null;

        record.Excluded = excluded;
        await _dbContext.SaveChangesAsync(ct);
        _logger.LogInformation("Event {EventUniqueId} {Action} del calendario",
            eventUniqueId, excluded ? "excluido" : "reincluido");

        return await _eventQueryService.GetByUniqueIdAsync(eventUniqueId, ct);
    }

    public async Task<bool> DeleteAsync(string eventUniqueId, CancellationToken ct = default)
    {
        var record = await _dbContext.EventRecords
            .FirstOrDefaultAsync(e => e.EventUniqueId == eventUniqueId, ct);
        if (record == null)
            return false;

        // Cross-matches referencing the event must not stay behind as stale rows.
        var matches = await _dbContext.CrossMatches
            .Where(m => m.EventUniqueId == eventUniqueId)
            .ToListAsync(ct);
        _dbContext.CrossMatches.RemoveRange(matches);
        _dbContext.EventRecords.Remove(record);

        await _dbContext.SaveChangesAsync(ct);
        _logger.LogInformation("Deleted event {EventUniqueId} ({Title})", eventUniqueId, record.Title);
        return true;
    }

    private static string? NullIfWhitespace(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value;
}
