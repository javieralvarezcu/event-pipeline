using EventPipeline.Core.Entities;

namespace EventPipeline.Core.Features.Events;

/// <summary>
/// Shared date/recurrence helpers for the persisted events, reused by recognition,
/// cleanup and queries. Day-granularity logic lives in <see cref="RecurrenceEvaluator"/>.
/// </summary>
public static class EventOccurrence
{
    /// <summary>
    /// Whether the event (or one of its recurrences) occurs on any day of [from, to].
    /// </summary>
    public static bool OccursInRange(EventRecord r, DateTime from, DateTime to)
    {
        var analysis = new PostAnalysisResult
        {
            EventDate = r.EventDate?.ToString("yyyy-MM-dd"),
            RecurrenceStartDate = r.RecurrenceStartDate?.ToString("yyyy-MM-dd"),
            RecurrenceEndDate = r.RecurrenceEndDate?.ToString("yyyy-MM-dd"),
            RecurrenceDaysOfWeek = ParseRecurrenceDays(r.RecurrenceDaysOfWeek)
        };
        return RecurrenceEvaluator.OccursInRange(analysis, from, to);
    }

    /// <summary>Whether the event has no computable date at all.</summary>
    public static bool HasNoDate(EventRecord r)
        => r.EventDate == null && r.RecurrenceStartDate == null && r.RecurrenceEndDate == null;

    /// <summary>Maps a persisted event to the shape the LLM duplicate/match prompts use.</summary>
    public static CleanupEventItem ToCleanupItem(EventRecord r)
        => new()
        {
            EventUniqueId = r.EventUniqueId,
            Title = r.Title,
            Summary = r.Summary,
            EventDate = r.EventDate,
            EventDateDescription = r.EventDateDescription,
            IsRecurrent = r.IsRecurrent,
            RecurrenceType = r.RecurrenceType,
            RecurrenceDaysOfWeek = r.RecurrenceDaysOfWeek,
            RecurrenceStartDate = r.RecurrenceStartDate,
            RecurrenceEndDate = r.RecurrenceEndDate,
            Account = r.Account,
            Caption = r.Caption,
            Url = r.Url
        };

    public static List<int>? ParseRecurrenceDays(string? days)
    {
        if (string.IsNullOrWhiteSpace(days))
            return null;

        return days.Split(',')
            .Select(s => int.TryParse(s.Trim(), out var n) ? n : (int?)null)
            .Where(n => n.HasValue)
            .Select(n => n!.Value)
            .ToList();
    }
}
