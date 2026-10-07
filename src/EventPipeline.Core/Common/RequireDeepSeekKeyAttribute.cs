using EventPipeline.Core.Contracts;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace EventPipeline.Core.Common;

/// <summary>
/// Requires the X-DeepSeek-API-Key header on an endpoint. It is the cost barrier of
/// the LLM for REST callers: without a valid key, nobody can trigger LLM calls
/// through the API. The web UI authenticates with the session cookie and uses the
/// key stored encrypted in AppSettings instead. The REST layer never stores the
/// key — it only forwards it to DeepSeek.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public sealed class RequireDeepSeekKeyAttribute : Attribute, IActionFilter
{
    public const string DeepSeekApiKeyHeader = "X-DeepSeek-API-Key";

    public void OnActionExecuting(ActionExecutingContext context)
    {
        var key = context.HttpContext.Request.Headers[DeepSeekApiKeyHeader].FirstOrDefault();
        if (string.IsNullOrWhiteSpace(key))
        {
            context.Result = new UnauthorizedObjectResult(new ErrorResponse
            {
                Error = "Missing API key",
                Detail = $"The '{DeepSeekApiKeyHeader}' header is required with a valid DeepSeek API key."
            });
        }
    }

    public void OnActionExecuted(ActionExecutedContext context)
    {
    }
}
