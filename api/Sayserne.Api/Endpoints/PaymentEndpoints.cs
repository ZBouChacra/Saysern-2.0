using Sayserne.Api.Auth;
using Sayserne.Api.Payments;

namespace Sayserne.Api.Endpoints;

/// <summary>Receipts: listing, one receipt in full (for printing), correcting who it is made out to, and totals. Receipts are created by marking a batch paid, never directly.</summary>
public static class PaymentEndpoints
{
    public sealed record BillingBody(string? BilledTo, string? BilledDetails, string? Notes);

    private static AppException Invalid(string field, string message) => new("VALIDATION_FAILED", 400, fieldErrors: new Dictionary<string, string> { [field] = message });

    /// <summary>The billing details required to mark a batch paid (validated again in the database).</summary>
    public static BillingInput RequireBilling(string? billedTo, string? details, string? method, string? reference, DateOnly? receivedOn, string? notes)
    {
        if (string.IsNullOrWhiteSpace(billedTo)) throw Invalid("billedTo", "Who is this receipt made out to?");
        if (billedTo.Trim().Length > 200) throw Invalid("billedTo", "At most 200 characters.");
        if (string.IsNullOrWhiteSpace(method)) throw Invalid("method", "How was it paid?");
        if (details?.Length > 500 || notes?.Length > 500) throw Invalid("notes", "At most 500 characters.");
        if (reference?.Length > 100) throw Invalid("reference", "At most 100 characters.");
        return new BillingInput(billedTo.Trim(), details, method.Trim(), reference, receivedOn, notes);
    }

    public static void MapPayments(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/admin/payments");
        g.MapGet("", async (IPaymentRepository payments, string? status, string? kind, DateOnly? from, DateOnly? to, string? search, int? page, int? pageSize) =>
            Results.Ok(await payments.ListAsync(Trim(status, 10), Trim(kind, 10), from, to, Trim(search, 100), page ?? 1, pageSize ?? 25))).RequirePermission("payments.read");
        g.MapGet("/summary", async (IPaymentRepository payments, DateOnly? from, DateOnly? to) => Results.Ok(await payments.SummaryAsync(from, to))).RequirePermission("payments.read");
        g.MapGet("/{id:long}", async (long id, IPaymentRepository payments) => Results.Ok(await payments.GetAsync(id) ?? throw new AppException("PAYMENT_NOT_FOUND", 404))).RequirePermission("payments.read");
        g.MapPut("/{id:long}/billing", async (HttpContext ctx, long id, BillingBody b, IPaymentRepository payments) =>
        {
            if (string.IsNullOrWhiteSpace(b.BilledTo)) throw Invalid("billedTo", "Who is this receipt made out to?");
            if (b.BilledTo.Trim().Length > 200) throw Invalid("billedTo", "At most 200 characters.");
            if (b.BilledDetails?.Length > 500 || b.Notes?.Length > 500) throw Invalid("notes", "At most 500 characters.");
            return Results.Ok(await payments.UpdateBillingAsync(ctx.UserId(), id, b.BilledTo.Trim(), b.BilledDetails, b.Notes));
        }).RequirePermission("payments.manage");
    }

    private static string? Trim(string? s, int max) => string.IsNullOrWhiteSpace(s) ? null : s.Trim().Length > max ? throw Invalid("filter", $"At most {max} characters.") : s.Trim();
}
