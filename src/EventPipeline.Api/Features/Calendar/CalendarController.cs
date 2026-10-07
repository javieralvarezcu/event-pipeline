using System.Text;
using EventPipeline.Core.Contracts;
using EventPipeline.Core.Entities;
using EventPipeline.Core.Features.Calendar;
using EventPipeline.Core.Features.Events;
using Microsoft.AspNetCore.Mvc;

namespace EventPipeline.Api.Features.Calendar;

[Route("api/calendar")]
public class CalendarController : ControllerBase
{
    private readonly IEventQueryService _eventQueryService;
    private readonly IcsCalendarBuilder _icsBuilder;

    public CalendarController(IEventQueryService eventQueryService, IcsCalendarBuilder icsBuilder)
    {
        _eventQueryService = eventQueryService;
        _icsBuilder = icsBuilder;
    }

    /// <summary>
    /// The muxo jaleo calendar as an ICS file for the given range (default: today to
    /// +90 days). Served on demand; the calendar page links to it and any calendar
    /// app can subscribe to the URL.
    /// </summary>
    [HttpGet("ics")]
    [ProducesResponseType(typeof(FileContentResult), StatusCodes.Status200OK, "text/calendar")]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> GetIcs(
        [FromQuery] DateTime? from = null,
        [FromQuery] DateTime? to = null,
        CancellationToken ct = default)
    {
        var fromDate = from?.Date ?? DateTime.UtcNow.Date;
        var toDate = to?.Date ?? fromDate.AddDays(90);

        if (fromDate > toDate)
        {
            return BadRequest(new ErrorResponse
            {
                Error = "Invalid date range",
                Detail = "'from' must be earlier than or equal to 'to'."
            });
        }

        var events = await _eventQueryService.GetAllAsync(new DateRange(fromDate, toDate), includeExcluded: false, ct);
        var ics = _icsBuilder.Build(events);

        return File(
            Encoding.UTF8.GetBytes(ics),
            "text/calendar; charset=utf-8",
            $"muxo-jaleo-{fromDate:yyyy-MM-dd}-{toDate:yyyy-MM-dd}.ics");
    }
}
