using System.Globalization;
using EventPipeline.Core.Contracts;

namespace EventPipeline.Web.Components.Calendar;

/// <summary>
/// A FullCalendar-ready event (allDay, "YYYY-MM-DD" dates so no timezone shifting).
/// Kind is "ours" (editable/deletable) or "muxo" (read-only). Subtitle is the
/// second line rendered inside the calendar cell.
/// </summary>
public record CalendarEventDto(
    string Title,
    string Start,
    string? End,
    string? StartRecur,
    string? EndRecur,
    int[]? DaysOfWeek,
    string? BackgroundColor,
    string Kind,
    string Key,
    string? Subtitle);

/// <summary>
/// C# port of the date/calendar helpers of the old static panel (app.js): same
/// logic, same Spanish formats, but unit-testable.
/// </summary>
public static class CalendarEventMapper
{
    public static readonly string[] DayNames =
        { "lunes", "martes", "miércoles", "jueves", "viernes", "sábado", "domingo" }; // índice = n - 1

    private const string CrossedGreen = "#2e7d4f";
    private const string MuxoPurple = "#7c3aed";

    /// <summary>"2026-09-19T20:00:00" → "2026-09-19" (fecha, sin hora).</summary>
    public static string? ToDateOnly(DateTime? dt) => dt?.ToString("yyyy-MM-dd");

    /// <summary>Suma días a una fecha "YYYY-MM-DD" con aritmética local.</summary>
    public static string AddDays(string dateOnly, int n)
    {
        var date = DateOnly.ParseExact(dateOnly, "yyyy-MM-dd");
        return date.AddDays(n).ToString("yyyy-MM-dd");
    }

    /// <summary>"2026-09-19" → "19 de septiembre de 2026".</summary>
    public static string FormatDateOnly(string dateOnly)
    {
        var date = DateOnly.ParseExact(dateOnly, "yyyy-MM-dd");
        return date.ToString("d 'de' MMMM 'de' yyyy", new CultureInfo("es-ES"));
    }

    /// <summary>
    /// Converts one of our events into a FullCalendar event, or null when it has no
    /// computable date (then it belongs to the "Eventos sin fecha" list).
    /// </summary>
    public static CalendarEventDto? BuildCalendarEvent(EventDetailResponse e)
    {
        var title = string.IsNullOrEmpty(e.Title) ? "Sin título" : e.Title;
        string? background = e.IsCrossed ? CrossedGreen : null;

        if (e.RecurrenceType == "weekly")
        {
            var days = (e.RecurrenceDaysOfWeek ?? string.Empty)
                .Split(',')
                .Select(s => int.TryParse(s.Trim(), out var n) ? n : (int?)null)
                .Where(n => n.HasValue)
                .Select(n => n!.Value)
                .ToList();
            var startRecur = ToDateOnly(e.RecurrenceStartDate) ?? ToDateOnly(e.EventDate);
            if (days.Count == 0 || startRecur == null)
                return null;

            var endRecur = ToDateOnly(e.RecurrenceEndDate);
            if (endRecur != null && string.CompareOrdinal(endRecur, startRecur) <= 0)
                endRecur = null; // si end <= start, FullCalendar no renderiza nada

            return new CalendarEventDto(
                title, startRecur, null, startRecur, endRecur,
                days.Select(n => n % 7).ToArray(), // BD: 1=lunes...7=domingo → FC: 0=domingo...6=sábado
                background, "ours", e.EventUniqueId, "Semanal");
        }

        if ((e.RecurrenceType == "daily" || e.IsRecurrent)
            && (e.RecurrenceStartDate != null || e.RecurrenceEndDate != null))
        {
            // Rango de varios días: un único evento que abarca el tramo.
            var start = ToDateOnly(e.RecurrenceStartDate) ?? ToDateOnly(e.EventDate);
            var end = ToDateOnly(e.RecurrenceEndDate);
            if (start == null && end == null)
                return null;

            var effectiveStart = start ?? end!;
            // El final de FullCalendar es exclusivo: "del 10 al 12" necesita end = día 13.
            var exclusiveEnd = end != null && start != null && string.CompareOrdinal(end, start) > 0
                ? AddDays(end, 1)
                : AddDays(effectiveStart, 1);

            var subtitle = end != null && start != null && string.CompareOrdinal(end, start) > 0
                ? $"{FormatDateOnly(start)} – {FormatDateOnly(end)}"
                : null;

            return new CalendarEventDto(
                title, effectiveStart, exclusiveEnd, null, null, null,
                background, "ours", e.EventUniqueId, subtitle);
        }

        var startOnly = ToDateOnly(e.EventDate);
        if (startOnly == null)
            return null;

        return new CalendarEventDto(
            title, startOnly, null, null, null, null,
            background, "ours", e.EventUniqueId, null);
    }

    /// <summary>Converts an uncrossed muxojaleo event into a purple FullCalendar event.</summary>
    public static CalendarEventDto? BuildMuxoCalendarEvent(MuxoEventDto m)
    {
        var start = ToDateOnly(m.Date);
        if (start == null)
            return null;

        return new CalendarEventDto(
            string.IsNullOrEmpty(m.Title) ? "Sin título" : m.Title,
            start, null, null, null, null,
            MuxoPurple, "muxo", m.ExternalId, null);
    }

    /// <summary>Humaniza la recurrencia en español para el modal.</summary>
    public static string FormatRecurrence(EventDetailResponse e)
    {
        var desde = e.RecurrenceStartDate != null
            ? $", desde {FormatDateOnly(ToDateOnly(e.RecurrenceStartDate)!)}"
            : string.Empty;
        var hasta = e.RecurrenceEndDate != null
            ? $", hasta {FormatDateOnly(ToDateOnly(e.RecurrenceEndDate)!)}"
            : string.Empty;

        if (e.RecurrenceType == "weekly")
        {
            var days = (e.RecurrenceDaysOfWeek ?? string.Empty)
                .Split(',')
                .Select(s => int.TryParse(s.Trim(), out var n) ? n : 0)
                .Where(n => n >= 1 && n <= 7)
                .ToList();
            if (days.Count > 0)
                return $"Semanal: {string.Join(", ", days.Select(n => DayNames[n - 1]))}{desde}{hasta}";
            return $"Semanal{desde}{hasta}";
        }

        if (e.RecurrenceType == "daily")
        {
            if (e.RecurrenceStartDate != null && e.RecurrenceEndDate != null)
            {
                return $"Diario, del {FormatDateOnly(ToDateOnly(e.RecurrenceStartDate)!)} " +
                       $"al {FormatDateOnly(ToDateOnly(e.RecurrenceEndDate)!)}";
            }
            return $"Diario{desde}{hasta}";
        }

        return e.IsRecurrent ? $"Evento recurrente{desde}{hasta}" : "No es recurrente";
    }
}
