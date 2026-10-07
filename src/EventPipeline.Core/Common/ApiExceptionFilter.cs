using EventPipeline.Core.Contracts;
using EventPipeline.Core.Features.Muxo;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.EntityFrameworkCore;

namespace EventPipeline.Core.Common;

/// <summary>
/// Central error mapping, replacing per-action try/catch:
/// LLM HTTP failures → 502, malformed LLM responses → 500, scrape failures → 502,
/// database failures → 500. Always the ErrorResponse shape ({error, detail}).
/// </summary>
public sealed class ApiExceptionFilter : IExceptionFilter
{
    private readonly ILogger<ApiExceptionFilter> _logger;

    public ApiExceptionFilter(ILogger<ApiExceptionFilter> logger)
    {
        _logger = logger;
    }

    public void OnException(ExceptionContext context)
    {
        var response = context.Exception switch
        {
            ScrapeException ex => Error(StatusCodes.Status502BadGateway,
                "Failed to scrape muxojaleo.com", ex.Message),
            HttpRequestException ex => Error(StatusCodes.Status502BadGateway,
                "Failed to communicate with the LLM service", ex.Message),
            InvalidOperationException ex => Error(StatusCodes.Status500InternalServerError,
                "Failed to process the LLM response", ex.Message),
            DbUpdateException ex => Error(StatusCodes.Status500InternalServerError,
                "Failed to save events to the database", ex.Message),
            _ => null
        };

        if (response == null)
            return;

        _logger.LogError(context.Exception, "Request failed: {Message}", context.Exception.Message);
        context.Result = response;
        context.ExceptionHandled = true;
    }

    private static ObjectResult Error(int statusCode, string error, string detail)
        => new(new ErrorResponse { Error = error, Detail = detail }) { StatusCode = statusCode };
}
