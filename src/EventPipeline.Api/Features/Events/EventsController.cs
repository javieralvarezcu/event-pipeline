using EventPipeline.Core.Common;
using EventPipeline.Core.Contracts;
using EventPipeline.Core.Features.Events;
using EventPipeline.Core.Entities;
using Microsoft.AspNetCore.Mvc;

namespace EventPipeline.Api.Features.Events;

[Route("api/events")]
public class EventsController : ControllerBase
{
    private readonly IEventQueryService _eventQueryService;
    private readonly IEventCrudService _eventCrudService;
    private readonly ICleanupService _cleanupService;

    public EventsController(
        IEventQueryService eventQueryService,
        IEventCrudService eventCrudService,
        ICleanupService cleanupService)
    {
        _eventQueryService = eventQueryService;
        _eventCrudService = eventCrudService;
        _cleanupService = cleanupService;
    }

    /// <summary>All persisted events, optionally filtered to the ones occurring in a range.</summary>
    [HttpGet]
    [ProducesResponseType(typeof(List<EventDetailResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> GetEvents(
        [FromQuery] DateTime? dateFrom = null,
        [FromQuery] DateTime? dateTo = null,
        CancellationToken ct = default)
    {
        if (dateFrom.HasValue && dateTo.HasValue && dateFrom > dateTo)
        {
            return BadRequest(new ErrorResponse
            {
                Error = "Invalid date range",
                Detail = "'dateFrom' must be earlier than or equal to 'dateTo'."
            });
        }

        var dateRange = dateFrom.HasValue || dateTo.HasValue
            ? new DateRange(dateFrom, dateTo)
            : null;

        var events = await _eventQueryService.GetAllAsync(dateRange, includeExcluded: false, ct);
        return Ok(events);
    }

    /// <summary>One persisted event with its muxo cross-match, by unique id.</summary>
    [HttpGet("{eventUniqueId}")]
    [ProducesResponseType(typeof(EventDetailResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetEvent(string eventUniqueId, CancellationToken ct = default)
    {
        var result = await _eventQueryService.GetByUniqueIdAsync(eventUniqueId, ct);
        if (result == null)
        {
            return NotFound(new ErrorResponse
            {
                Error = "Event not found",
                Detail = $"No event exists with unique id '{eventUniqueId}'."
            });
        }

        return Ok(result);
    }

    /// <summary>Updates the editable fields of a persisted event.</summary>
    [HttpPut("{eventUniqueId}")]
    [RequireDeepSeekKey]
    [ProducesResponseType(typeof(EventDetailResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> UpdateEvent(
        string eventUniqueId,
        [FromBody] UpdateEventRequest request,
        CancellationToken ct = default)
    {
        if (!ModelState.IsValid)
        {
            return BadRequest(new ErrorResponse
            {
                Error = "Invalid request body",
                Detail = "The request body has validation errors."
            });
        }

        var result = await _eventCrudService.UpdateAsync(eventUniqueId, request, ct);
        if (result == null)
        {
            return NotFound(new ErrorResponse
            {
                Error = "Event not found",
                Detail = $"No event exists with unique id '{eventUniqueId}'."
            });
        }

        return Ok(result);
    }

    /// <summary>Deletes a persisted event and its cross-matches.</summary>
    [HttpDelete("{eventUniqueId}")]
    [RequireDeepSeekKey]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> DeleteEvent(string eventUniqueId, CancellationToken ct = default)
    {
        var deleted = await _eventCrudService.DeleteAsync(eventUniqueId, ct);
        if (!deleted)
        {
            return NotFound(new ErrorResponse
            {
                Error = "Event not found",
                Detail = $"No event exists with unique id '{eventUniqueId}'."
            });
        }

        return NoContent();
    }

    /// <summary>
    /// Asks the LLM which persisted events of the month (plus the undated ones) are
    /// duplicates of the same real event, and deletes the duplicates.
    /// </summary>
    [HttpPost("cleanup")]
    [RequireDeepSeekKey]
    [ProducesResponseType(typeof(CleanupResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> CleanupMonth(
        [FromQuery] string? month = null,
        CancellationToken ct = default)
    {
        if (!TryParseMonth(month, out var year, out var monthNumber))
        {
            return BadRequest(new ErrorResponse
            {
                Error = "Invalid month",
                Detail = "'month' must use the yyyy-MM format."
            });
        }

        var deepSeekApiKey = Request.Headers[RequireDeepSeekKeyAttribute.DeepSeekApiKeyHeader].FirstOrDefault()
            ?? string.Empty;

        var result = await _cleanupService.CleanupMonthAsync(year, monthNumber, deepSeekApiKey, ct);
        return Ok(result);
    }

    private static bool TryParseMonth(string? month, out int year, out int monthNumber)
    {
        year = 0;
        monthNumber = 0;

        if (string.IsNullOrWhiteSpace(month))
            return false;

        var parts = month.Split('-');
        if (parts.Length != 2)
            return false;

        if (!int.TryParse(parts[0], out year) || !int.TryParse(parts[1], out monthNumber))
            return false;

        return year is >= 2000 and <= 2100 && monthNumber is >= 1 and <= 12;
    }
}
