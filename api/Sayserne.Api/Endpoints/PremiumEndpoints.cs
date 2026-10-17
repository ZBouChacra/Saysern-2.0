using Sayserne.Api.Auth;
using Sayserne.Api.Premium;

namespace Sayserne.Api.Endpoints;

/// <summary>Staff-only management of premium listings: batches of paid days, prices and per-country limits.</summary>
public static class PremiumEndpoints
{
    public sealed record BatchInput(long? ProfessionId, DateOnly? From, DateOnly? To);
    public sealed record BatchStatusInput(string? Status, string? BilledTo, string? BilledDetails, string? Method, string? Reference, DateOnly? ReceivedOn, string? Notes);
    public sealed record PricingInput(string? CountryCode, string? City, decimal? DailyFee, int? MaxPremium);

    private static AppException Invalid(string field, string message) => new("VALIDATION_FAILED", 400, fieldErrors: new Dictionary<string, string> { [field] = message });
    private static string? Clean(string? s, int max, string field)
    {
        var t = string.IsNullOrWhiteSpace(s) ? null : s.Trim();
        if (t is { } v && v.Length > max) throw Invalid(field, $"At most {max} characters.");
        return t;
    }

    public static void MapPremium(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/admin/premium");
        g.MapGet("/batches", async (IPremiumRepository premium, string? status, string? country) => Results.Ok(await premium.BatchesAsync(Clean(status, 10, "status"), Clean(country, 100, "country")))).RequirePermission("premium.read");
        g.MapGet("/find", async (IPremiumRepository premium, string? search) => Results.Ok(await premium.FindAsync(Clean(search, 100, "search"), 20))).RequirePermission("premium.read");
        g.MapGet("/pricing", async (IPremiumRepository premium) => Results.Ok(await premium.PricingAsync())).RequirePermission("premium.read");
        g.MapGet("/pricing/resolve", async (IPremiumRepository premium, string country, string? city) => Results.Ok(await premium.ResolveAsync(country, Clean(city, 100, "city")))).RequirePermission("premium.read");
        g.MapGet("/capacity", async (IPremiumRepository premium, string country, string? city, DateOnly from, DateOnly to) => Results.Ok(await premium.CapacityAsync(country, Clean(city, 100, "city"), from, to))).RequirePermission("premium.read");

        g.MapPost("/batches", async (HttpContext ctx, BatchInput b, IPremiumRepository premium) =>
        {
            if (b.ProfessionId is null) throw Invalid("professionId", "Choose a listing.");
            if (b.From is null || b.To is null) throw Invalid("from", "Choose the first and last day.");
            return Results.Json(await premium.CreateBatchAsync(ctx.UserId(), b.ProfessionId.Value, b.From.Value, b.To.Value), statusCode: StatusCodes.Status201Created);
        }).RequirePermission("premium.manage");
        g.MapPost("/batches/{id:long}/status", async (HttpContext ctx, long id, BatchStatusInput b, IPremiumRepository premium) =>
        {
            var status = (b.Status ?? "").Trim().ToLowerInvariant();
            var billing = status == "paid" ? PaymentEndpoints.RequireBilling(b.BilledTo, b.BilledDetails, b.Method, b.Reference, b.ReceivedOn, b.Notes) : null;
            return Results.Ok(await premium.SetBatchStatusAsync(ctx.UserId(), id, status, billing));
        }).RequirePermission("premium.manage");

        g.MapPut("/pricing", async (HttpContext ctx, PricingInput b, IPremiumRepository premium) =>
        {
            var country = Clean(b.CountryCode, 100, "countryCode") ?? throw Invalid("countryCode", "Choose a country.");
            return Results.Ok(await premium.SavePricingAsync(ctx.UserId(), country, Clean(b.City, 100, "city"), b.DailyFee, b.MaxPremium));
        }).RequirePermission("premium.manage");
        g.MapDelete("/pricing/{id:long}", async (HttpContext ctx, long id, IPremiumRepository premium) =>
        {
            await premium.DeletePricingAsync(ctx.UserId(), id);
            return Results.NoContent();
        }).RequirePermission("premium.manage");
    }
}
