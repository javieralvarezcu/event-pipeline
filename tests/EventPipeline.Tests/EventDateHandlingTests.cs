using EventPipeline.Core.Entities;
using EventPipeline.Core.Features.Posts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace EventPipeline.Tests;

/// <summary>
/// Regression: the event date must be stored EXACTLY as the poster states it — no
/// UTC conversion (a local midnight used to shift the event one day back) and the
/// optional poster time (event_time) is combined with the date as-is.
/// </summary>
public class EventDateHandlingTests
{
    [Fact]
    public async Task DateOnly_IsStoredWithoutAnyTimezoneShift()
    {
        using var fixture = new TestDatabase();
        var registry = new FakePostRegistryService();
        // El LLM devuelve SOLO la fecha (tras el fix del prompt): antes, el
        // ToUniversalTime() la convertía a UTC y el evento caía un día antes.
        var deepSeek = new FakeDeepSeekService(analyze: (_, _) => new List<PostAnalysisResult>
        {
            TestData.EventResult(
                title: "FLORA OFF 2026",
                eventDate: "2026-10-12",
                dateDescription: "Del 12 al 22 de octubre",
                eventTime: null)
        });
        var recognition = new RecognitionService(deepSeek, registry, fixture.Db, NullLogger<RecognitionService>.Instance);
        var post = TestData.CreatePost("p-1", caption: "FLORA OFF · Del 12 al 22 de octubre");

        await recognition.RecognizeAsync(new List<InstagramPost> { post }, "sk-test");

        var stored = await fixture.Db.EventRecords.SingleAsync();
        Assert.Equal(new DateTime(2026, 10, 12), stored.EventDate!.Value.Date);
        Assert.Equal(TimeSpan.Zero, stored.EventDate.Value.TimeOfDay);
    }

    [Fact]
    public async Task ExplicitPosterTime_IsCombinedWithTheDate()
    {
        using var fixture = new TestDatabase();
        var registry = new FakePostRegistryService();
        var deepSeek = new FakeDeepSeekService(analyze: (_, _) => new List<PostAnalysisResult>
        {
            TestData.EventResult(
                title: "Concierto",
                eventDate: "2026-10-17",
                dateDescription: "Sábado 17 de octubre",
                eventTime: "21:30")
        });
        var recognition = new RecognitionService(deepSeek, registry, fixture.Db, NullLogger<RecognitionService>.Instance);

        await recognition.RecognizeAsync(
            new List<InstagramPost> { TestData.CreatePost("p-1") }, "sk-test");

        var stored = await fixture.Db.EventRecords.SingleAsync();
        Assert.Equal(new DateTime(2026, 10, 17, 21, 30, 0), stored.EventDate);
    }
}
