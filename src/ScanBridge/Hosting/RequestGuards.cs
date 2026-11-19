using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace ScanBridge.Hosting;

/// <summary>
/// Defences against web pages driving the local scanner: Host validation (DNS rebinding)
/// and a mandatory custom header on state-changing requests (forces a CORS preflight).
/// </summary>
public static class RequestGuards
{
    public const string RequiredHeaderName = "X-ScanBridge";

    public static bool IsAllowedHost(string? host, int port) =>
        string.Equals(host, $"127.0.0.1:{port}", StringComparison.OrdinalIgnoreCase)
        || string.Equals(host, $"localhost:{port}", StringComparison.OrdinalIgnoreCase);

    public static bool RequiresCustomHeader(HttpRequest request) =>
        (HttpMethods.IsPost(request.Method) || HttpMethods.IsDelete(request.Method))
        && request.Path.StartsWithSegments("/api/v1", StringComparison.OrdinalIgnoreCase);

    /// <summary>Rejects requests whose Host header is not the loopback address on our port.</summary>
    public static IApplicationBuilder UseHostValidation(this IApplicationBuilder app, int port) =>
        app.Use(async (context, next) =>
        {
            if (!IsAllowedHost(context.Request.Headers.Host.ToString(), port))
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                await context.Response.WriteAsync("Intestazione Host non valida.");
                return;
            }

            await next();
        });

    /// <summary>Rejects POST/DELETE on /api/v1 that lack the X-ScanBridge header. Run after UseCors.</summary>
    public static IApplicationBuilder UseRequiredHeader(this IApplicationBuilder app) =>
        app.Use(async (context, next) =>
        {
            if (RequiresCustomHeader(context.Request) && !context.Request.Headers.ContainsKey(RequiredHeaderName))
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                await context.Response.WriteAsync($"Manca l'intestazione {RequiredHeaderName}.");
                return;
            }

            await next();
        });
}
