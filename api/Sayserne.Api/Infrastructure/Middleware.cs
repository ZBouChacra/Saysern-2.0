using Microsoft.Extensions.Options;

namespace Sayserne.Api.Infrastructure;

/// <summary>
/// CSRF defence for the cookie session. State-changing requests must come from an allowed origin, or
/// (when the browser sent no Origin) carry the custom header that cross-site forms cannot set.
/// SameSite=Lax on the session cookie is the second layer.
/// </summary>
public sealed class CsrfMiddleware(RequestDelegate next, IConfiguration config)
{
    public const string HeaderName = "X-Requested-With";
    private readonly string[] _allowed = [.. config.GetSection("Web:AllowedOrigins").Get<string[]>() ?? [], .. config.GetSection("Admin:AllowedOrigins").Get<string[]>() ?? []];

    public async Task InvokeAsync(HttpContext ctx)
    {
        var method = ctx.Request.Method;
        var unsafeMethod = !(HttpMethods.IsGet(method) || HttpMethods.IsHead(method) || HttpMethods.IsOptions(method));
        if (unsafeMethod && ctx.Request.Path.StartsWithSegments("/api"))
        {
            var origin = ctx.Request.Headers.Origin.ToString();
            var sameOrigin = $"{ctx.Request.Scheme}://{ctx.Request.Host}";
            var ok = origin.Length > 0
                ? origin.Equals(sameOrigin, StringComparison.OrdinalIgnoreCase) || _allowed.Contains(origin, StringComparer.OrdinalIgnoreCase)
                : ctx.Request.Headers.ContainsKey(HeaderName);
            if (!ok)
            {
                ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
                await ctx.Response.WriteAsJsonAsync(new { error = "CSRF_REJECTED" });
                return;
            }
        }
        await next(ctx);
    }
}

public sealed class ErrorHandlingMiddleware(RequestDelegate next, ILogger<ErrorHandlingMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext ctx)
    {
        try { await next(ctx); }
        catch (AppException ex) when (!ctx.Response.HasStarted)
        {
            ctx.Response.StatusCode = ex.Status;
            await ctx.Response.WriteAsJsonAsync(new { error = ex.Code, errors = ex.FieldErrors });
        }
        catch (Exception ex) when (!ctx.Response.HasStarted)
        {
            logger.LogError(ex, "Unhandled error for {Method} {Path}", ctx.Request.Method, ctx.Request.Path);
            ctx.Response.StatusCode = StatusCodes.Status500InternalServerError;
            await ctx.Response.WriteAsJsonAsync(new { error = "SERVER_ERROR" });
        }
    }
}

public sealed class SecurityHeadersMiddleware(RequestDelegate next)
{
    public Task InvokeAsync(HttpContext ctx)
    {
        var h = ctx.Response.Headers;
        h["X-Content-Type-Options"] = "nosniff";
        h["X-Frame-Options"] = "DENY";
        h["Referrer-Policy"] = "strict-origin-when-cross-origin";
        h["Cache-Control"] = "no-store";
        return next(ctx);
    }
}
