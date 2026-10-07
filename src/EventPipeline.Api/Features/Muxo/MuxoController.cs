using EventPipeline.Core.Common;
using EventPipeline.Core.Contracts;
using EventPipeline.Core.Features.Muxo;
using Microsoft.AspNetCore.Mvc;

namespace EventPipeline.Api.Features.Muxo;

[Route("api/muxo")]
public class MuxoController : ControllerBase
{
    private readonly IMuxoSyncService _muxoSyncService;
    private readonly ICrossCheckService _crossCheckService;
    private readonly IMuxoQueryService _muxoQueryService;

    public MuxoController(
        IMuxoSyncService muxoSyncService,
        ICrossCheckService crossCheckService,
        IMuxoQueryService muxoQueryService)
    {
        _muxoSyncService = muxoSyncService;
        _crossCheckService = crossCheckService;
        _muxoQueryService = muxoQueryService;
    }

    /// <summary>
    /// Scrapes the muxojaleo.com calendar and upserts the events (no LLM involved).
    /// n8n orchestrates this before the crosscheck.
    /// </summary>
    [HttpPost("sync")]
    [ProducesResponseType(typeof(MuxoSyncResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status502BadGateway)]
    public async Task<IActionResult> Sync(
        [FromQuery] int monthsAhead = 2,
        CancellationToken ct = default)
    {
        var clamped = Math.Clamp(monthsAhead, 0, 6);
        var result = await _muxoSyncService.SyncAsync(clamped, ct);
        return Ok(result);
    }

    /// <summary>
    /// Asks the LLM to match our persisted events with the persisted muxojaleo.com
    /// events and stores the new matches. Does not scrape: run sync first.
    /// </summary>
    [HttpPost("crosscheck")]
    [RequireDeepSeekKey]
    [ProducesResponseType(typeof(CrossCheckResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> CrossCheck(CancellationToken ct = default)
    {
        var deepSeekApiKey = Request.Headers[RequireDeepSeekKeyAttribute.DeepSeekApiKeyHeader].FirstOrDefault()
            ?? string.Empty;

        var result = await _crossCheckService.CrossCheckAsync(deepSeekApiKey, ct);
        return Ok(result);
    }

    /// <summary>All persisted muxojaleo.com events, with whether each one is already crossed.</summary>
    [HttpGet]
    [ProducesResponseType(typeof(List<MuxoEventDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetMuxoEvents(CancellationToken ct = default)
    {
        var events = await _muxoQueryService.GetAllAsync(ct);
        return Ok(events);
    }
}
