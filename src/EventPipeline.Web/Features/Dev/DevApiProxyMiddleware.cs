using System.Net.Http.Headers;

namespace EventPipeline.Web.Features.Dev;

/// <summary>
/// Solo en Desarrollo: reenvía /api/* y /swagger* a la Api local (puerto 5200),
/// replicando el enrutado por path que hace cloudflared en producción. Así los
/// enlaces relativos (p. ej. /api/calendar/ics) funcionan igual en dev que en prod.
/// </summary>
public class DevApiProxyMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<DevApiProxyMiddleware> _logger;

    public DevApiProxyMiddleware(RequestDelegate next, ILogger<DevApiProxyMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var path = context.Request.Path;
        if (!path.StartsWithSegments("/api") && !path.StartsWithSegments("/swagger"))
        {
            await _next(context);
            return;
        }

        var target = new Uri(
            $"http://localhost:5200{path}{context.Request.QueryString}");
        using var request = new HttpRequestMessage(new HttpMethod(context.Request.Method), target);

        // Copia headers de la petición (incluido X-DeepSeek-API-Key), salvo Host
        // y los de contenido, que van en el body.
        foreach (var header in context.Request.Headers)
        {
            if (header.Key.Equals("Host", StringComparison.OrdinalIgnoreCase)
                || header.Key.StartsWith("Content-", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            request.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray());
        }

        var hasBody = context.Request.ContentLength is > 0 || context.Request.Method is "POST" or "PUT" or "PATCH";
        if (hasBody)
        {
            request.Content = new StreamContent(context.Request.Body);
            if (MediaTypeHeaderValue.TryParse(context.Request.ContentType, out var contentType))
                request.Content.Headers.ContentType = contentType;
        }

        HttpResponseMessage response;
        try
        {
            response = await new HttpClient().SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, context.RequestAborted);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "DevApiProxy no pudo conectar con la Api en localhost:5200. " +
                                   "¿Está arrancada? (perfil 'Web + API' o multi-startup en Visual Studio)");
            context.Response.StatusCode = StatusCodes.Status502BadGateway;
            await context.Response.WriteAsync(
                "La API REST no responde en http://localhost:5200. Arranca también el proyecto EventPipeline.Api.");
            return;
        }

        using (response)
        {
            context.Response.StatusCode = (int)response.StatusCode;
            foreach (var header in response.Headers)
            {
                if (header.Key.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase))
                    continue;
                context.Response.Headers[header.Key] = header.Value.ToArray();
            }

            foreach (var header in response.Content.Headers)
            {
                if (header.Key.Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
                    continue;
                context.Response.Headers[header.Key] = header.Value.ToArray();
            }

            await response.Content.CopyToAsync(context.Response.Body, context.RequestAborted);
        }
    }
}
