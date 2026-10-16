using Sayserne.Api.Platform;

namespace Sayserne.Api.Endpoints;

/// <summary>What the web app needs to render itself: public settings, lookups and translations. Nothing is hard-coded in the client.</summary>
public static class PublicConfigEndpoints
{
    private static void Cache(HttpContext ctx, int seconds) => ctx.Response.Headers.CacheControl = $"public, max-age={seconds}";

    public static void MapPublicConfig(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/config/public", (HttpContext ctx, ISettingsService settings) =>
        {
            Cache(ctx, 10);   // short: an admin's change to a rule should reach visitors within seconds
            return Results.Ok(settings.PublicValues());
        });

        // Active values of a lookup (every language's name), optionally the children of one parent (cities of a country).
        app.MapGet("/api/lookups/{type}", async (HttpContext ctx, string type, IPlatformRepository platform, string? parent) =>
        {
            var values = await platform.LookupValuesAsync(type, parent, includeInactive: false);
            if (values.Count == 0 && !(await platform.LookupTypesAsync()).Any(t => t.Code == type)) throw new AppException("NOT_FOUND");
            Cache(ctx, 60);
            return Results.Ok(values);
        });

        app.MapGet("/api/translations/{language}", async (HttpContext ctx, string language, IPlatformRepository platform) =>
        {
            if (!(await platform.LookupValuesAsync("language", null, includeInactive: false)).Any(l => l.Code == language)) throw new AppException("NOT_FOUND");
            Cache(ctx, 60);
            return Results.Ok(await platform.TranslationsAsync(language));
        });
    }
}
