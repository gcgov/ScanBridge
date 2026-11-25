using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Net.Http.Headers;
using ScanBridge.Api;
using ScanBridge.Localization;

namespace ScanBridge.Hosting;

/// <summary>
/// Defences against web pages driving the local scanner: Host validation (DNS rebinding)
/// and an origin check on state-changing requests. Neither one needs a change in a
/// client that already works.
/// </summary>
public static class RequestGuards
{
    public static bool IsAllowedHost(string? host, int port) =>
        string.Equals(host, $"127.0.0.1:{port}", StringComparison.OrdinalIgnoreCase)
        || string.Equals(host, $"localhost:{port}", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// True for a POST or DELETE under /api/v1 that a browser sent from a page whose origin
    /// is not in the allowlist.
    /// </summary>
    /// <remarks>
    /// CORS alone does not stop such a request. A "simple" cross-origin POST (no custom
    /// header, a text/plain body or no body) skips the preflight. The browser then hides
    /// the response from the page, but the scan has already started. Browsers send an
    /// Origin header on every cross-origin POST and DELETE, so this check closes that gap.
    /// Requests without an Origin header (curl, desktop apps) are not affected.
    /// </remarks>
    public static bool IsDisallowedBrowserWrite(HttpRequest request, IReadOnlyCollection<string> allowedOrigins)
    {
        if (!HttpMethods.IsPost(request.Method) && !HttpMethods.IsDelete(request.Method))
        {
            return false;
        }

        if (!request.Path.StartsWithSegments("/api/v1", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var origin = request.Headers[HeaderNames.Origin].ToString();
        if (string.IsNullOrEmpty(origin))
        {
            return false;
        }

        return !allowedOrigins.Contains(origin, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Rejects requests whose Host header is not the loopback address on our port.</summary>
    public static IApplicationBuilder UseHostValidation(this IApplicationBuilder app, int port) =>
        app.Use(async (context, next) =>
        {
            if (!IsAllowedHost(context.Request.Headers.Host.ToString(), port))
            {
                await WriteErrorAsync(context, ApiErrorCodes.InvalidHost, Strings.ApiInvalidHost);
                return;
            }

            await next();
        });

    /// <summary>
    /// Rejects POST/DELETE requests from browser origins outside the allowlist. Run it after
    /// UseCors, so that a preflight from an allowed origin is still answered.
    /// </summary>
    public static IApplicationBuilder UseOriginCheck(this IApplicationBuilder app, IReadOnlyCollection<string> allowedOrigins) =>
        app.Use(async (context, next) =>
        {
            if (IsDisallowedBrowserWrite(context.Request, allowedOrigins))
            {
                var origin = context.Request.Headers[HeaderNames.Origin].ToString();
                await WriteErrorAsync(context, ApiErrorCodes.OriginNotAllowed, Strings.ApiOriginNotAllowed(origin));
                return;
            }

            await next();
        });

    private static Task WriteErrorAsync(HttpContext context, string code, string message)
    {
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        return context.Response.WriteAsJsonAsync(new ApiErrorResponse(new ApiErrorBody(code, message)));
    }
}
