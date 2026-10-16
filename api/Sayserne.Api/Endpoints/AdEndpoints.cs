using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Sayserne.Api.Ads;
using Sayserne.Api.Auth;
using Sayserne.Api.Data;
using Sayserne.Api.Jobs;
using Sayserne.Api.Media;
using Sayserne.Api.Platform;

namespace Sayserne.Api.Endpoints;

/// <summary>Public ad serving and tracking, and the staff-only management of ads, prices and batches.</summary>
public static partial class AdEndpoints
{
    public sealed record AdInput(string? Title, string? Position, string? ImageFileId, string? LinkUrl, string? CountryCode, string? City, bool? IsActive, decimal? FeeOverride);
    public sealed record BatchInput(DateOnly? From, DateOnly? To);
    public sealed record BatchStatusInput(string? Status);
    public sealed record PricingInput(string? Position, string? CountryCode, string? City, decimal? DailyFee, int? MaxAds);

    public const string EventLimiter = "ads";
    private const string SessionCookie = "sayserne.sid";

    [GeneratedRegex("^[a-z][a-z0-9_]{0,29}$")] private static partial Regex PositionPattern();

    private static AppException Invalid(string field, string message) => new("VALIDATION_FAILED", 400, fieldErrors: new Dictionary<string, string> { [field] = message });
    private static string? Clean(string? s, int max, string field)
    {
        var t = string.IsNullOrWhiteSpace(s) ? null : s.Trim();
        if (t is { } v && v.Length > max) throw Invalid(field, $"At most {max} characters.");
        return t;
    }

    /// <summary>An anonymous, random visitor id kept in a cookie. Only its hash is stored, so the same visitor is counted once per day without storing who they are (no IP address is stored).</summary>
    private static string VisitorHash(HttpContext ctx, IWebHostEnvironment env)
    {
        if (!ctx.Request.Cookies.TryGetValue(SessionCookie, out var id) || id.Length is < 16 or > 64)
        {
            id = Convert.ToBase64String(RandomNumberGenerator.GetBytes(16)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
            ctx.Response.Cookies.Append(SessionCookie, id, new CookieOptions
            {
                HttpOnly = true, SameSite = SameSiteMode.Lax, Secure = !env.IsDevelopment(), MaxAge = TimeSpan.FromDays(365), IsEssential = true,
            });
        }
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(id)))[..16].ToLowerInvariant();
    }

    public static void MapAds(this IEndpointRouteBuilder app)
    {
        var pub = app.MapGroup("/api/ads");
        pub.MapGet("", async (IAdRepository ads, ISettingsService settings, string? position, string? country, string? city) =>
        {
            if (position is null || !PositionPattern().IsMatch(position)) throw Invalid("position", "Choose a position.");
            return Results.Ok(await ads.ServeAsync(position, Clean(country, 100, "country"), Clean(city, 100, "city"), settings.GetInt("Ads.MaxPerSlot")));
        });

        pub.MapPost("/{id:long}/impression", async (HttpContext ctx, long id, IAdRepository ads, IWebHostEnvironment env) =>
        {
            await ads.RecordAsync(id, "impression", VisitorHash(ctx, env));
            return Results.NoContent();
        }).RequireRateLimiting(EventLimiter);

        // A normal link: the click is counted, then the visitor is sent on. The destination is whatever staff saved (checked to be http/https when saved).
        pub.MapGet("/{id:long}/click", async (HttpContext ctx, long id, IAdRepository ads, IWebHostEnvironment env) =>
        {
            var link = await ads.RecordAsync(id, "click", VisitorHash(ctx, env));
            ctx.Response.Headers["Referrer-Policy"] = "no-referrer";
            return Results.Redirect(link, permanent: false);
        }).RequireRateLimiting(EventLimiter);

        var g = app.MapGroup("/api/admin/ads");
        g.MapGet("", async (IAdRepository ads, string? position) => Results.Ok(await ads.AdsAsync(position))).RequirePermission("ads.read");
        g.MapGet("/pricing", async (IAdRepository ads) => Results.Ok(await ads.PricingAsync())).RequirePermission("ads.read");
        g.MapGet("/pricing/resolve", async (IAdRepository ads, string position, string? country, string? city) => Results.Ok(await ads.ResolveAsync(position, Clean(country, 100, "country"), Clean(city, 100, "city")))).RequirePermission("ads.read");
        g.MapGet("/batches", async (IAdRepository ads, long? adId, string? status) => Results.Ok(await ads.BatchesAsync(adId, status))).RequirePermission("ads.read");
        g.MapGet("/stats", async (IAdRepository ads, DateOnly? from, DateOnly? to) => Results.Ok(await ads.StatsAsync(from, to))).RequirePermission("ads.read");
        g.MapGet("/{id:long}/stats", async (long id, IAdRepository ads, DateOnly? from, DateOnly? to) => Results.Ok(await ads.DailyAsync(id, from, to))).RequirePermission("ads.read");
        g.MapGet("/{id:long}/capacity", async (long id, IAdRepository ads, DateOnly from, DateOnly to) => Results.Ok(await ads.CapacityAsync(id, from, to))).RequirePermission("ads.read");

        g.MapPost("", async (HttpContext ctx, AdInput b, IAdRepository ads) =>
            Results.Json(await Save(ctx, null, b, ads), statusCode: StatusCodes.Status201Created)).RequirePermission("ads.manage");
        g.MapPut("/{id:long}", async (HttpContext ctx, long id, AdInput b, IAdRepository ads) => Results.Ok(await Save(ctx, id, b, ads))).RequirePermission("ads.manage");

        g.MapPost("/{id:long}/batches", async (HttpContext ctx, long id, BatchInput b, IAdRepository ads) =>
        {
            if (b.From is null || b.To is null) throw Invalid("from", "Choose the first and last day.");
            return Results.Json(await ads.CreateBatchAsync(ctx.UserId(), id, b.From.Value, b.To.Value), statusCode: StatusCodes.Status201Created);
        }).RequirePermission("ads.manage");
        g.MapPost("/batches/{id:long}/status", async (HttpContext ctx, long id, BatchStatusInput b, IAdRepository ads) =>
            Results.Ok(await ads.SetBatchStatusAsync(ctx.UserId(), id, (b.Status ?? "").Trim().ToLowerInvariant()))).RequirePermission("ads.manage");

        g.MapPut("/pricing", async (HttpContext ctx, PricingInput b, IAdRepository ads) =>
        {
            if (string.IsNullOrWhiteSpace(b.Position)) throw Invalid("position", "Choose a position.");
            return Results.Ok(await ads.SavePricingAsync(ctx.UserId(), b.Position.Trim(), Clean(b.CountryCode, 100, "countryCode"), Clean(b.City, 100, "city"), b.DailyFee, b.MaxAds));
        }).RequirePermission("ads.manage");
        g.MapDelete("/pricing/{id:long}", async (HttpContext ctx, long id, IAdRepository ads) =>
        {
            await ads.DeletePricingAsync(ctx.UserId(), id);
            return Results.NoContent();
        }).RequirePermission("ads.manage");

        // The ad's picture: same checks as every upload (type by its bytes, shrunk, metadata removed), stored as a public "ad_image".
        g.MapPost("/images", (HttpContext ctx, IFileRepository files, IStorageRouter storage, MediaIngestService ingest, IJobQueue jobs, ISettingsService settings, TimeProvider clock) =>
            FileEndpoints.UploadCore(ctx, "ad_image", null, files, storage, ingest, jobs, settings, clock, allowStaffOnly: true)).RequirePermission("ads.manage");
    }

    private static async Task<Ad> Save(HttpContext ctx, long? id, AdInput b, IAdRepository ads)
    {
        var title = Clean(b.Title, 150, "title") ?? throw Invalid("title", "Required.");
        var position = Clean(b.Position, 30, "position") ?? throw Invalid("position", "Choose a position.");
        var image = Clean(b.ImageFileId, 32, "imageFileId") ?? throw Invalid("imageFileId", "Upload the ad's picture first.");
        var link = Clean(b.LinkUrl, 500, "linkUrl") ?? throw Invalid("linkUrl", "Required.");
        return await ads.SaveAdAsync(ctx.UserId(), id, title, position, image, link, Clean(b.CountryCode, 100, "countryCode"), Clean(b.City, 100, "city"), b.IsActive, b.FeeOverride);
    }
}
