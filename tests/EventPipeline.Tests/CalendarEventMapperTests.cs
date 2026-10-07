using EventPipeline.Web.Components.Calendar;
using EventPipeline.Core.Contracts;

namespace EventPipeline.Tests;

public class CalendarEventMapperTests
{
    private static EventDetailResponse Event(
        DateTime? eventDate = null,
        bool isCrossed = false,
        bool isRecurrent = false,
        string? recurrenceType = null,
        string? recurrenceDaysOfWeek = null,
        DateTime? recurrenceStart = null,
        DateTime? recurrenceEnd = null,
        string title = "Concierto")
        => new()
        {
            EventUniqueId = "EVT-test",
            Title = title,
            EventDate = eventDate,
            IsCrossed = isCrossed,
            IsRecurrent = isRecurrent,
            RecurrenceType = recurrenceType,
            RecurrenceDaysOfWeek = recurrenceDaysOfWeek,
            RecurrenceStartDate = recurrenceStart,
            RecurrenceEndDate = recurrenceEnd,
        };

    private static MuxoEventDto Muxo(DateTime? date, string title = "Fiesta muxo", string externalId = "m-1")
        => new() { ExternalId = externalId, Title = title, Date = date };

    [Fact]
    public void SingleEvent_UsesItsDateAllDay()
    {
        var dto = CalendarEventMapper.BuildCalendarEvent(Event(eventDate: new DateTime(2026, 9, 19, 22, 0, 0)));

        Assert.NotNull(dto);
        Assert.Equal("2026-09-19", dto!.Start);
        Assert.Null(dto.End);
        Assert.Equal("ours", dto.Kind);
        Assert.Null(dto.BackgroundColor);
    }

    [Fact]
    public void CrossedEvent_IsPaintedGreen()
    {
        var dto = CalendarEventMapper.BuildCalendarEvent(
            Event(eventDate: new DateTime(2026, 9, 19), isCrossed: true));

        Assert.Equal("#2e7d4f", dto!.BackgroundColor);
    }

    [Fact]
    public void WeeklyEvent_ConvertsDaysAndRecurrenceBounds()
    {
        var dto = CalendarEventMapper.BuildCalendarEvent(Event(
            isRecurrent: true,
            recurrenceType: "weekly",
            recurrenceDaysOfWeek: "4,7", // jueves + domingo (BD: 1=lunes...7=domingo)
            recurrenceStart: new DateTime(2026, 9, 10),
            recurrenceEnd: new DateTime(2027, 1, 10)));

        Assert.NotNull(dto);
        Assert.Equal("2026-09-10", dto!.Start);
        Assert.Equal("2026-09-10", dto.StartRecur);
        Assert.Equal("2027-01-10", dto.EndRecur);
        Assert.Equal(new[] { 4, 0 }, dto.DaysOfWeek); // 7 % 7 = 0 (domingo)
        Assert.Equal("Semanal", dto.Subtitle);
    }

    [Fact]
    public void WeeklyEvent_DropsEndRecurWhenNotAfterTheStart()
    {
        var dto = CalendarEventMapper.BuildCalendarEvent(Event(
            isRecurrent: true,
            recurrenceType: "weekly",
            recurrenceDaysOfWeek: "4",
            recurrenceStart: new DateTime(2026, 9, 10),
            recurrenceEnd: new DateTime(2026, 9, 1))); // end <= start: FullCalendar no renderizaría

        Assert.NotNull(dto);
        Assert.Null(dto!.EndRecur);
    }

    [Fact]
    public void WeeklyEvent_WithoutDaysOrStartFallsToTheNoDateList()
    {
        Assert.Null(CalendarEventMapper.BuildCalendarEvent(Event(isRecurrent: true, recurrenceType: "weekly")));
        Assert.Null(CalendarEventMapper.BuildCalendarEvent(Event(
            isRecurrent: true, recurrenceType: "weekly", recurrenceDaysOfWeek: "4")));
    }

    [Fact]
    public void DailyRange_UsesExclusiveEnd()
    {
        var dto = CalendarEventMapper.BuildCalendarEvent(Event(
            isRecurrent: true,
            recurrenceType: "daily",
            recurrenceStart: new DateTime(2026, 9, 10),
            recurrenceEnd: new DateTime(2026, 9, 12)));

        Assert.NotNull(dto);
        Assert.Equal("2026-09-10", dto!.Start);
        Assert.Equal("2026-09-13", dto.End); // exclusivo: "del 10 al 12" → end = 13
        Assert.Equal("10 de septiembre de 2026 – 12 de septiembre de 2026", dto.Subtitle);
    }

    [Fact]
    public void DailyRange_WithoutEndSpansOneDay()
    {
        var dto = CalendarEventMapper.BuildCalendarEvent(Event(
            isRecurrent: true,
            recurrenceType: "daily",
            recurrenceStart: new DateTime(2026, 9, 10)));

        Assert.NotNull(dto);
        Assert.Equal("2026-09-10", dto!.Start);
        Assert.Equal("2026-09-11", dto.End);
        Assert.Null(dto.Subtitle);
    }

    [Fact]
    public void UndatedEvent_IsNull()
    {
        Assert.Null(CalendarEventMapper.BuildCalendarEvent(Event()));
    }

    [Fact]
    public void MuxoEvent_IsPurpleAndReadOnly()
    {
        var dto = CalendarEventMapper.BuildMuxoCalendarEvent(Muxo(new DateTime(2026, 9, 21)));

        Assert.NotNull(dto);
        Assert.Equal("2026-09-21", dto!.Start);
        Assert.Equal("#7c3aed", dto.BackgroundColor);
        Assert.Equal("muxo", dto.Kind);
        Assert.Equal("m-1", dto.Key);
    }

    [Fact]
    public void MuxoEvent_WithoutDateIsNull()
    {
        Assert.Null(CalendarEventMapper.BuildMuxoCalendarEvent(Muxo(null)));
    }

    [Theory]
    [InlineData("weekly", "4", "Semanal: jueves")]
    [InlineData("weekly", "1,7", "Semanal: lunes, domingo")]
    [InlineData("weekly", null, "Semanal")]
    [InlineData("daily", null, "Diario, del 10 de septiembre de 2026 al 12 de septiembre de 2026")]
    public void FormatRecurrence_HumanizesSpanishPatterns(string type, string? days, string expected)
    {
        var e = Event(
            isRecurrent: true,
            recurrenceType: type,
            recurrenceDaysOfWeek: days,
            recurrenceStart: type == "daily" ? new DateTime(2026, 9, 10) : null,
            recurrenceEnd: type == "daily" ? new DateTime(2026, 9, 12) : null);

        Assert.Equal(expected, CalendarEventMapper.FormatRecurrence(e));
    }

    [Fact]
    public void FormatRecurrence_NonRecurrentEvent()
    {
        Assert.Equal("No es recurrente", CalendarEventMapper.FormatRecurrence(Event(eventDate: new DateTime(2026, 9, 1))));
    }

    [Theory]
    [InlineData("2026-09-19", "19 de septiembre de 2026")]
    [InlineData("2026-01-02", "2 de enero de 2026")]
    public void FormatDateOnly_UsesSpanishLongFormat(string input, string expected)
    {
        Assert.Equal(expected, CalendarEventMapper.FormatDateOnly(input));
    }

    [Theory]
    [InlineData("2026-12-31", 1, "2027-01-01")]
    [InlineData("2026-03-01", -1, "2026-02-28")]
    public void AddDays_HandlesMonthBoundaries(string input, int n, string expected)
    {
        Assert.Equal(expected, CalendarEventMapper.AddDays(input, n));
    }
}
