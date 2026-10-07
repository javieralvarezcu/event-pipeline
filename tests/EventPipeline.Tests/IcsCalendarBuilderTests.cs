using EventPipeline.Core.Contracts;
using EventPipeline.Core.Features.Calendar;

namespace EventPipeline.Tests;

public class IcsCalendarBuilderTests
{
    private readonly IcsCalendarBuilder _builder = new();

    private static EventDetailResponse Event(
        string id,
        string title,
        DateTime? eventDate = null,
        bool recurrent = false,
        string? recurrenceType = null,
        string? days = null,
        DateTime? start = null,
        DateTime? end = null,
        string summary = "Resumen",
        string caption = "Caption",
        string url = "https://instagram.com/p/x",
        string account = "test.account")
        => new()
        {
            EventUniqueId = id,
            Title = title,
            Summary = summary,
            Caption = caption,
            Url = url,
            Account = account,
            EventDate = eventDate,
            IsRecurrent = recurrent,
            RecurrenceType = recurrenceType,
            RecurrenceDaysOfWeek = days,
            RecurrenceStartDate = start,
            RecurrenceEndDate = end
        };

    [Fact]
    public void Build_SingleEventWithTime_UsesUtcDateTimeStart()
    {
        var ics = _builder.Build(new[]
        {
            Event("EVT-1", "Concierto", eventDate: new DateTime(2026, 9, 19, 20, 0, 0, DateTimeKind.Utc))
        });

        Assert.Contains("BEGIN:VEVENT", ics);
        Assert.Contains("DTSTART:20260919T200000Z", ics);
        Assert.DoesNotContain("RRULE", ics);
        Assert.Contains("UID:EVT-1@eventpipeline-api", ics);
    }

    [Fact]
    public void Build_SingleAllDayEvent_UsesDateStart()
    {
        var ics = _builder.Build(new[]
        {
            Event("EVT-1", "Feria", eventDate: new DateTime(2026, 10, 2))
        });

        Assert.Contains("DTSTART;VALUE=DATE:20261002", ics);
    }

    [Fact]
    public void Build_WeeklyEvent_UsesByDayRule()
    {
        var ics = _builder.Build(new[]
        {
            Event("EVT-1", "Jueves Cong",
                recurrent: true, recurrenceType: "weekly", days: "4",
                start: new DateTime(2026, 9, 10))
        });

        Assert.Contains("RRULE:FREQ=WEEKLY;BYDAY=TH", ics);
        Assert.Contains("DTSTART;VALUE=DATE:20260910", ics);
        Assert.DoesNotContain("UNTIL", ics); // open-ended weekly
    }

    [Fact]
    public void Build_WeeklyEventWithEnd_IncludesUntil()
    {
        var ics = _builder.Build(new[]
        {
            Event("EVT-1", "Jueves de verano",
                recurrent: true, recurrenceType: "weekly", days: "4",
                start: new DateTime(2026, 7, 2), end: new DateTime(2026, 9, 24))
        });

        Assert.Contains("RRULE:FREQ=WEEKLY;UNTIL=20260924T000000;BYDAY=TH", ics);
    }

    [Fact]
    public void Build_DailyRange_UsesDailyRuleWithUntil()
    {
        var ics = _builder.Build(new[]
        {
            Event("EVT-1", "Feria de Córdoba",
                recurrent: true, recurrenceType: "daily",
                start: new DateTime(2026, 5, 23), end: new DateTime(2026, 5, 30))
        });

        Assert.Contains("RRULE:FREQ=DAILY;UNTIL=20260530", ics);
        Assert.Contains("DTSTART;VALUE=DATE:20260523", ics);
    }

    [Fact]
    public void Build_DailyWithoutEnd_FallsBackToSingleEvent()
    {
        var ics = _builder.Build(new[]
        {
            Event("EVT-1", "Ciclo sin fin",
                recurrent: true, recurrenceType: "daily",
                start: new DateTime(2026, 9, 1))
        });

        Assert.DoesNotContain("RRULE", ics); // no infinite daily rule
        Assert.Contains("DTSTART;VALUE=DATE:20260901", ics);
    }

    [Fact]
    public void Build_UndatedEvents_AreExcluded()
    {
        var ics = _builder.Build(new[]
        {
            Event("EVT-1", "Sin fecha ninguna"),
            Event("EVT-2", "Con fecha", eventDate: new DateTime(2026, 9, 19))
        });

        Assert.DoesNotContain("Sin fecha ninguna", ics);
        Assert.Contains("Con fecha", ics);
    }

    [Fact]
    public void Build_MultipleDaysInWeekly_AreAllInByDay()
    {
        var ics = _builder.Build(new[]
        {
            Event("EVT-1", "De lunes a jueves",
                recurrent: true, recurrenceType: "weekly", days: "1,2,3,4",
                start: new DateTime(2026, 9, 14))
        });

        Assert.Contains("RRULE:FREQ=WEEKLY;BYDAY=MO,TU,WE,TH", ics);
    }

    [Fact]
    public void Build_IncludesDescriptionWithCaptionUrlAndAccount()
    {
        var ics = _builder.Build(new[]
        {
            Event("EVT-1", "Concierto", eventDate: new DateTime(2026, 9, 19, 20, 0, 0, DateTimeKind.Utc),
                summary: "Gran concierto", caption: "No te lo pierdas", url: "https://instagram.com/p/abc", account: "salaimpala")
        });

        Assert.Contains("Gran concierto", ics);
        Assert.Contains("No te lo pierdas", ics);
        // Long DESCRIPTION lines are folded at 75 chars: unfold before asserting.
        Assert.Contains("https://instagram.com/p/abc", ics.Replace("\r\n ", ""));
        Assert.Contains("salaimpala", ics.Replace("\r\n ", ""));
    }
}
