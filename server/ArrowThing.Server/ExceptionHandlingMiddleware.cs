using System.Text.Json;
using Serilog.Context;

namespace ArrowThing.Server;

/// <summary>
/// Stamps every request with a correlation id (prefers the caller's
/// <c>X-Correlation-Id</c> header, falls back to a fresh GUID), pushes it
/// onto the Serilog <see cref="LogContext"/> for the request scope, and
/// emits it on the response header so clients can quote it in bug reports.
///
/// Also catches any unhandled exception and returns a consistent
/// <c>500 { error, correlationId }</c> body after logging. Intentional 4xx
/// and 5xx responses produced by endpoints pass through untouched.
/// </summary>
public class ExceptionHandlingMiddleware
{
    internal const string CorrelationIdHeader = "X-Correlation-Id";
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly RequestDelegate _next;
    private readonly ILogger<ExceptionHandlingMiddleware> _logger;

    public ExceptionHandlingMiddleware(
        RequestDelegate next,
        ILogger<ExceptionHandlingMiddleware> logger
    )
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var correlationId = ResolveCorrelationId(context);
        context.Response.Headers[CorrelationIdHeader] = correlationId;

        using (LogContext.PushProperty("CorrelationId", correlationId))
        {
            try
            {
                await _next(context);
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "Unhandled exception on {Method} {Path}",
                    context.Request.Method,
                    context.Request.Path
                );

                if (context.Response.HasStarted)
                {
                    // Can't rewrite the response — log and give up. The client
                    // will see the connection reset instead of a clean 500.
                    throw;
                }

                context.Response.Clear();
                // Clear() drops the header we set above, so re-stamp it.
                context.Response.Headers[CorrelationIdHeader] = correlationId;
                context.Response.StatusCode = StatusCodes.Status500InternalServerError;
                context.Response.ContentType = "application/json; charset=utf-8";
                await JsonSerializer.SerializeAsync(
                    context.Response.Body,
                    new { error = "Internal server error.", correlationId },
                    JsonOptions
                );
            }
        }
    }

    private static string ResolveCorrelationId(HttpContext context)
    {
        var fromHeader = context.Request.Headers[CorrelationIdHeader].FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(fromHeader) && fromHeader.Length <= 128)
            return fromHeader;
        return Guid.NewGuid().ToString("N");
    }
}
