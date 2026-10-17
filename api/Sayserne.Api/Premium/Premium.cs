using System.Data.Common;
using System.Text.Json;
using Sayserne.Api.Data;
using Sayserne.Api.Jobs;

namespace Sayserne.Api.Premium;

public sealed record PremiumPricing(long Id, string CountryCode, string? City, decimal? DailyFee, int? MaxPremium);
public sealed record ResolvedPremiumPricing(decimal DailyFee, int MaxPerCountry, int? MaxPerCity);
public sealed record PremiumBatch(long Id, long? ProfessionId, long? UserId, string ProfessionalName, string ServiceName, string CountryCode, string? City, DateOnly DateFrom, DateOnly DateTo,
    int EntitledDays, int DaysAssigned, DateOnly? EffectiveTo, decimal FeePerDay, decimal TotalFee, string Status, DateTime? PaidAt, DateTime CreatedAt, bool ActiveToday);
public sealed record PremiumCandidate(long ProfessionId, long UserId, string FirstName, string LastName, string ServiceName, string? Country, string? City, bool IsPremium);
public sealed record PremiumCapacityDay(DateOnly Day, int Used, int MaxPremium, int? CityUsed, int? MaxCity);

public interface IPremiumRepository
{
    Task<IReadOnlyList<PremiumPricing>> PricingAsync(CancellationToken ct = default);
    Task<PremiumPricing> SavePricingAsync(long actorId, string country, string? city, decimal? dailyFee, int? maxPremium, CancellationToken ct = default);
    Task DeletePricingAsync(long actorId, long id, CancellationToken ct = default);
    Task<ResolvedPremiumPricing> ResolveAsync(string country, string? city, CancellationToken ct = default);
    Task<IReadOnlyList<PremiumCandidate>> FindAsync(string? search, int limit, CancellationToken ct = default);
    Task<PremiumBatch> CreateBatchAsync(long actorId, long professionId, DateOnly from, DateOnly to, CancellationToken ct = default);
    Task<PremiumBatch> SetBatchStatusAsync(long actorId, long id, string status, CancellationToken ct = default);
    Task<IReadOnlyList<PremiumBatch>> BatchesAsync(string? status, string? country, CancellationToken ct = default);
    Task<IReadOnlyList<PremiumCapacityDay>> CapacityAsync(string country, string? city, DateOnly from, DateOnly to, CancellationToken ct = default);
    Task ReallocateAllAsync(CancellationToken ct = default);
}

public sealed class PremiumRepository(IStoredProcedureExecutor db) : IPremiumRepository
{
    private static DateOnly D(DbDataReader r, string name) => DateOnly.FromDateTime(r.GetDateTime(r.GetOrdinal(name)));
    private static DateOnly? DN(DbDataReader r, string name) => r.IsDBNull(r.GetOrdinal(name)) ? null : D(r, name);
    private static decimal M(DbDataReader r, string name) => r.GetDecimal(r.GetOrdinal(name));
    private static DateTime Day(DateOnly d) => d.ToDateTime(TimeOnly.MinValue);

    private static PremiumPricing Pricing(DbDataReader r) => new(r.Long("id"), r.Str("country_code"), r.StrOrNull("city"), r.DecOrNull("daily_fee"), r.IntOrNull("max_premium"));
    private static PremiumBatch Batch(DbDataReader r) => new(r.Long("id"), r.LongOrNull("profession_id"), r.LongOrNull("user_id"), r.Str("professional_name"), r.Str("service_name"), r.Str("country_code"), r.StrOrNull("city"),
        D(r, "date_from"), D(r, "date_to"), r.Int("entitled_days"), r.Int("days_assigned"), DN(r, "effective_to"), M(r, "fee_per_day"), M(r, "total_fee"), r.Str("status"), r.UtcOrNull("paid_at"), r.Utc("created_at"), r.Bool("active_today"));

    public Task<IReadOnlyList<PremiumPricing>> PricingAsync(CancellationToken ct = default) => db.QueryAsync("sp_premium_pricing_list", null, Pricing, ct);

    public Task<PremiumPricing> SavePricingAsync(long actorId, string country, string? city, decimal? dailyFee, int? maxPremium, CancellationToken ct = default) =>
        DeadlockRetry.RunAsync(async () => (await db.QuerySingleOrDefaultAsync("sp_premium_pricing_save", new ProcParams().Add("p_actor_id", actorId).Add("p_country", country).Add("p_city", city).Add("p_daily_fee", dailyFee).Add("p_max", maxPremium), Pricing, ct))!);

    public Task DeletePricingAsync(long actorId, long id, CancellationToken ct = default) =>
        DeadlockRetry.RunAsync(() => db.ExecuteAsync("sp_premium_pricing_delete", new ProcParams().Add("p_actor_id", actorId).Add("p_id", id), ct));

    public async Task<ResolvedPremiumPricing> ResolveAsync(string country, string? city, CancellationToken ct = default) =>
        (await db.QueryAsync("sp_premium_pricing_resolve", new ProcParams().Add("p_country", country).Add("p_city", city), r => new ResolvedPremiumPricing(M(r, "daily_fee"), r.Int("max_per_country"), r.IntOrNull("max_per_city")), ct))[0];

    public Task<IReadOnlyList<PremiumCandidate>> FindAsync(string? search, int limit, CancellationToken ct = default) =>
        db.QueryAsync("sp_premium_find_professions", new ProcParams().Add("p_search", search).Add("p_limit", limit),
            r => new PremiumCandidate(r.Long("profession_id"), r.Long("user_id"), r.Str("first_name"), r.Str("last_name"), r.Str("service_name"), r.StrOrNull("country"), r.StrOrNull("city"), r.Bool("is_premium")), ct);

    public async Task<PremiumBatch> CreateBatchAsync(long actorId, long professionId, DateOnly from, DateOnly to, CancellationToken ct = default) =>
        (await db.QuerySingleOrDefaultAsync("sp_premium_batches_create", new ProcParams().Add("p_actor_id", actorId).Add("p_profession_id", professionId).Add("p_from", Day(from)).Add("p_to", Day(to)), Batch, ct))!;

    public Task<PremiumBatch> SetBatchStatusAsync(long actorId, long id, string status, CancellationToken ct = default) =>
        DeadlockRetry.RunAsync(async () => (await db.QuerySingleOrDefaultAsync("sp_premium_batches_set_status", new ProcParams().Add("p_actor_id", actorId).Add("p_id", id).Add("p_status", status), Batch, ct))!);

    public Task<IReadOnlyList<PremiumBatch>> BatchesAsync(string? status, string? country, CancellationToken ct = default) =>
        db.QueryAsync("sp_premium_batches_list", new ProcParams().Add("p_status", status).Add("p_id", (long?)null).Add("p_country", country), Batch, ct);

    public Task<IReadOnlyList<PremiumCapacityDay>> CapacityAsync(string country, string? city, DateOnly from, DateOnly to, CancellationToken ct = default) =>
        db.QueryAsync("sp_premium_capacity_days", new ProcParams().Add("p_country", country).Add("p_city", city).Add("p_from", Day(from)).Add("p_to", Day(to)),
            r => new PremiumCapacityDay(D(r, "day"), r.Int("used"), r.Int("max_premium"), r.IntOrNull("city_used"), r.IntOrNull("max_city")), ct);

    public Task ReallocateAllAsync(CancellationToken ct = default) => DeadlockRetry.RunAsync(() => db.ExecuteAsync("sp_premium_reallocate_all", null, ct));
}

/// <summary>Daily housekeeping: re-checks who holds which premium days (dates pass, settings change). Safe to run any time.</summary>
public sealed class PremiumReallocateJobHandler(IPremiumRepository premium) : IJobHandler
{
    public const string JobType = "premium.reallocate";
    public string Type => JobType;
    public async Task HandleAsync(JobContext job, JsonElement payload, CancellationToken ct) => await premium.ReallocateAllAsync(ct);
}
