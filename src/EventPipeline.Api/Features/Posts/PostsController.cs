using EventPipeline.Core.Common;
using EventPipeline.Core.Contracts;
using EventPipeline.Core.Features.Posts;
using EventPipeline.Core.Entities;
using Microsoft.AspNetCore.Mvc;

namespace EventPipeline.Api.Features.Posts;

[Route("api/posts")]
public class PostsController : ControllerBase
{
    private readonly IRecognitionService _recognitionService;

    public PostsController(IRecognitionService recognitionService)
    {
        _recognitionService = recognitionService;
    }

    /// <summary>
    /// Recognizes events in the given Instagram posts. Posts whose URL is already in
    /// the registry (events and non-events) are returned from it without any LLM
    /// call; only unknown URLs are analyzed. Duplicates of an already persisted
    /// event (decided by the LLM) are not saved again.
    /// </summary>
    [HttpPost("recognize")]
    [RequireDeepSeekKey]
    [ProducesResponseType(typeof(RecognitionResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> Recognize(
        [FromBody] List<InstagramPost>? posts,
        [FromQuery] DateTime? dateFrom = null,
        [FromQuery] DateTime? dateTo = null,
        CancellationToken ct = default)
    {
        if (posts == null || posts.Count == 0)
        {
            return BadRequest(new ErrorResponse
            {
                Error = "No posts provided",
                Detail = "The request body must be a non-empty array of Instagram posts."
            });
        }

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

        var deepSeekApiKey = Request.Headers[RequireDeepSeekKeyAttribute.DeepSeekApiKeyHeader].FirstOrDefault()
            ?? string.Empty;

        var result = await _recognitionService.RecognizeAsync(posts, deepSeekApiKey, dateRange, ct);
        return Ok(result);
    }
}
