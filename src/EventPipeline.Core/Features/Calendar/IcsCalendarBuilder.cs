using EventPipeline.Core.Contracts;
using Ical.Net;
using Ical.Net.CalendarComponents;
using Ical.Net.DataTypes;
using Ical.Net.Serialization;

namespace EventPipeline.Core.Features.Calendar;

/// <summary>
/// Builds the ICS calendar of the persisted events: weekly recurrences become
/// RRULE BYDAY, daily ranges become RRULE with UNTIL, single events are one VEVENT.
/// Events without any computable date are excluded (they stay visible in the
/// calendar's "sin fecha" list).
/// </summary>
public sealed class IcsCalendarBuilder
{
    public string Build(IReadOnlyList<EventDetailResponse> events)
    {
        var calendar = new Ical.Net.Calendar();
        foreach (var e in events)
        {
            DateTime? start;
            if (e.IsRecurrent && e.RecurrenceStartDate.HasValue)
                start = e.RecurrenceStartDate.Value;
            else
                start = e.EventDate ?? e.RecurrenceStartDate;

            if (start is not { } startDate)
                continue; // no computable date: impossible to anchor a VEVENT

            var isAllDay = startDate.TimeOfDay == TimeSpan.Zero;
            var calStart = isAllDay
                ? new CalDateTime(startDate) { HasTime = false }
                : new CalDateTime(startDate, "UTC");
            var calEvent = new CalendarEvent
            {
                Uid = $"{e.EventUniqueId}@eventpipeline-api",
                Summary = e.Title,
                Description = BuildDescription(e),
                DtStart = calStart,
                DtStamp = new CalDateTime(DateTime.UtcNow, "UTC")
            };

            if (e.IsRecurrent && e.RecurrenceType == "weekly"
                && TryParseWeekDays(e.RecurrenceDaysOfWeek, out var weekDays))
            {
                // "Todos los jueves" → FREQ=WEEKLY;BYDAY=TH, open-ended unless the
                // recurrence has an end date. Open-ended rules are fine: the file is
                // regenerated on every publication.
                var rule = new RecurrencePattern(FrequencyType.Weekly) { ByDay = weekDays };
                if (e.RecurrenceEndDate.HasValue)
                    rule.Until = e.RecurrenceEndDate.Value;
                calEvent.RecurrenceRules.Add(rule);
            }
            else if (e.IsRecurrent && e.RecurrenceType == "daily" && e.RecurrenceEndDate.HasValue)
            {
                // "Del 14 al 17" → FREQ=DAILY;UNTIL=<fin>. Without an end date a daily
                // RRULE would be infinite: fall back to a single event at the start.
                var rule = new RecurrencePattern(FrequencyType.Daily)
                {
                    Until = e.RecurrenceEndDate.Value
                };
                calEvent.RecurrenceRules.Add(rule);
            }

            calendar.Events.Add(calEvent);
        }

        return new CalendarSerializer().SerializeToString(calendar);
    }

    private static string BuildDescription(EventDetailResponse e)
        => $"{e.Summary}\n\n{e.Caption}\n\n{e.Url} · {e.Account}";

    /// <summary>
    /// Converts the stored "1,2,3" (1 = Monday ... 7 = Sunday) into Ical.Net week days.
    /// </summary>
    private static bool TryParseWeekDays(string? daysOfWeek, out List<WeekDay> weekDays)
    {
        weekDays = new List<WeekDay>();
        if (string.IsNullOrWhiteSpace(daysOfWeek))
            return false;

        foreach (var part in daysOfWeek.Split(','))
        {
            if (!int.TryParse(part.Trim(), out var n) || n is < 1 or > 7)
                continue;
            // 1 = Monday ... 7 = Sunday → System.DayOfWeek (Sunday = 0).
            weekDays.Add(new WeekDay { DayOfWeek = (DayOfWeek)(n % 7) });
        }

        return weekDays.Count > 0;
    }
}
