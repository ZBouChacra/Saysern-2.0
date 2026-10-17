using System.Data.Common;
using Sayserne.Api.Data;

namespace Sayserne.Api.Payments;

/// <summary>Who paid and how, entered by staff when they mark a batch paid. This becomes the receipt.</summary>
public sealed record BillingInput(string? BilledTo, string? BilledDetails, string? Method, string? Reference, DateOnly? ReceivedOn, string? Notes);

public sealed record PaymentRow(long Id, string ReceiptNumber, string Kind, string Description, decimal Subtotal, decimal VatAmount, decimal Total, string Currency, string BilledTo, string Method,
    string? Reference, DateOnly ReceivedOn, string Status, DateTime IssuedAt);
public sealed record Receipt(long Id, long Seq, string ReceiptNumber, string Kind, long? AdBatchId, long? PremiumBatchId, string Description, int Quantity, decimal UnitPrice, decimal Subtotal,
    decimal VatPercent, decimal VatAmount, decimal Total, string Currency, string BilledTo, string? BilledDetails, string Method, string? MethodName, string? Reference, DateOnly ReceivedOn,
    string? Notes, string Status, string? VoidReason, DateTime? VoidedAt, DateTime IssuedAt, string CompanyName, string? CompanyAddress, string? CompanyTaxId, string? Footer);
public sealed record PaymentSummaryRow(string Kind, string Method, long Receipts, decimal Subtotal, decimal Vat, decimal Total);
public sealed record PaymentSummary(IReadOnlyList<PaymentSummaryRow> Rows, long Voided, decimal Total);

public interface IPaymentRepository
{
    Task<PagedResult<PaymentRow>> ListAsync(string? status, string? kind, DateOnly? from, DateOnly? to, string? search, int page, int pageSize, CancellationToken ct = default);
    Task<Receipt?> GetAsync(long id, CancellationToken ct = default);
    Task<Receipt> UpdateBillingAsync(long actorId, long id, string billedTo, string? details, string? notes, CancellationToken ct = default);
    Task<PaymentSummary> SummaryAsync(DateOnly? from, DateOnly? to, CancellationToken ct = default);
}

public sealed class PaymentRepository(IStoredProcedureExecutor db) : IPaymentRepository
{
    private static DateOnly D(DbDataReader r, string name) => DateOnly.FromDateTime(r.GetDateTime(r.GetOrdinal(name)));
    private static decimal M(DbDataReader r, string name) => r.GetDecimal(r.GetOrdinal(name));
    private static DateTime? Day(DateOnly? d) => d?.ToDateTime(TimeOnly.MinValue);

    private static PaymentRow Row(DbDataReader r) => new(r.Long("id"), r.Str("receipt_number"), r.Str("kind"), r.Str("description"), M(r, "subtotal"), M(r, "vat_amount"), M(r, "total"), r.Str("currency"),
        r.Str("billed_to"), r.Str("method"), r.StrOrNull("reference"), D(r, "received_on"), r.Str("status"), r.Utc("issued_at"));

    private static Receipt Full(DbDataReader r) => new(r.Long("id"), r.Long("seq"), r.Str("receipt_number"), r.Str("kind"), r.LongOrNull("ad_batch_id"), r.LongOrNull("premium_batch_id"), r.Str("description"), r.Int("quantity"),
        M(r, "unit_price"), M(r, "subtotal"), M(r, "vat_percent"), M(r, "vat_amount"), M(r, "total"), r.Str("currency"), r.Str("billed_to"), r.StrOrNull("billed_details"), r.Str("method"), r.StrOrNull("method_name"),
        r.StrOrNull("reference"), D(r, "received_on"), r.StrOrNull("notes"), r.Str("status"), r.StrOrNull("void_reason"), r.UtcOrNull("voided_at"), r.Utc("issued_at"), r.Str("company_name"),
        r.StrOrNull("company_address") is { Length: > 0 } a ? a : null, r.StrOrNull("company_tax_id") is { Length: > 0 } t ? t : null, r.StrOrNull("footer") is { Length: > 0 } f ? f : null);

    public async Task<PagedResult<PaymentRow>> ListAsync(string? status, string? kind, DateOnly? from, DateOnly? to, string? search, int page, int pageSize, CancellationToken ct = default)
    {
        var (rows, totals) = await db.QueryTwoAsync("sp_payments_list", new ProcParams().Add("p_status", status).Add("p_kind", kind).Add("p_from", Day(from)).Add("p_to", Day(to)).Add("p_search", search)
            .Add("p_page", page).Add("p_page_size", pageSize), Row, r => r.Long("total"), ct);
        return new PagedResult<PaymentRow>(rows, totals.Count > 0 ? totals[0] : 0, Math.Max(page, 1), Math.Clamp(pageSize, 1, 200));
    }

    public Task<Receipt?> GetAsync(long id, CancellationToken ct = default) => db.QuerySingleOrDefaultAsync("sp_payments_get", new ProcParams().Add("p_id", id), Full, ct);

    public async Task<Receipt> UpdateBillingAsync(long actorId, long id, string billedTo, string? details, string? notes, CancellationToken ct = default) =>
        (await db.QuerySingleOrDefaultAsync("sp_payments_update_billing", new ProcParams().Add("p_actor_id", actorId).Add("p_id", id).Add("p_billed_to", billedTo).Add("p_billed_details", details).Add("p_notes", notes), Full, ct))!;

    public async Task<PaymentSummary> SummaryAsync(DateOnly? from, DateOnly? to, CancellationToken ct = default)
    {
        var (rows, voided) = await db.QueryTwoAsync("sp_payments_summary", new ProcParams().Add("p_from", Day(from)).Add("p_to", Day(to)),
            r => new PaymentSummaryRow(r.Str("kind"), r.Str("method"), r.Long("receipts"), M(r, "subtotal"), M(r, "vat"), M(r, "total")), r => r.Long("voided"), ct);
        return new PaymentSummary(rows, voided.Count > 0 ? voided[0] : 0, rows.Sum(x => x.Total));
    }
}
