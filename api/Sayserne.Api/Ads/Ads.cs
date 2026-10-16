using System.Data.Common;
using Sayserne.Api.Data;
using Sayserne.Api.Jobs;
using Sayserne.Api.Platform;

namespace Sayserne.Api.Ads;

public sealed record AdPricing(long Id, string Position, string? CountryCode, string? City, decimal? DailyFee, int? MaxAds);
public sealed record ResolvedPricing(decimal DailyFee, int MaxAds);
public sealed record Ad(long Id, string Title, string Position, string ImageFileId, string LinkUrl, string? CountryCode, string? City, bool IsActive, decimal? FeeOverride, DateTime CreatedAt, bool RunningToday);
public sealed record AdBatch(long Id, long AdId, DateOnly DateFrom, DateOnly DateTo, decimal FeePerDay, decimal TotalFee, string Status, DateTime CreatedAt, DateTime? PaidAt);
public sealed record ServedAd(long Id, string Title, string Position, string ImageUrl, string ClickUrl);
public sealed record AdStat(long AdId, string Title, string Position, long Impressions, long UniqueImpressions, long Clicks, long UniqueClicks);
public sealed record AdDay(DateOnly Day, long Impressions, long UniqueImpressions, long Clicks, long UniqueClicks);
public sealed record CapacityDay(DateOnly Day, int Booked, int MaxAds);

public interface IAdRepository
{
    Task<IReadOnlyList<AdPricing>> PricingAsync(CancellationToken ct = default);
    Task<AdPricing> SavePricingAsync(long actorId, string position, string? country, string? city, decimal? dailyFee, int? maxAds, CancellationToken ct = default);
    Task DeletePricingAsync(long actorId, long id, CancellationToken ct = default);
    Task<ResolvedPricing> ResolveAsync(string position, string? country, string? city, CancellationToken ct = default);
    Task<Ad> SaveAdAsync(long actorId, long? id, string title, string position, string imageFileId, string linkUrl, string? country, string? city, bool? isActive, decimal? feeOverride, CancellationToken ct = default);
    Task<IReadOnlyList<Ad>> AdsAsync(string? position, CancellationToken ct = default);
    Task<IReadOnlyList<CapacityDay>> CapacityAsync(long adId, DateOnly from, DateOnly to, CancellationToken ct = default);
    Task<AdBatch> CreateBatchAsync(long actorId, long adId, DateOnly from, DateOnly to, CancellationToken ct = default);
    Task<AdBatch> SetBatchStatusAsync(long actorId, long id, string status, CancellationToken ct = default);
    Task<IReadOnlyList<AdBatch>> BatchesAsync(long? adId, string? status, CancellationToken ct = default);
    Task<IReadOnlyList<ServedAd>> ServeAsync(string position, string? country, string? city, int limit, CancellationToken ct = default);
    Task<string> RecordAsync(long adId, string kind, string sessionHash, CancellationToken ct = default);
    Task<IReadOnlyList<AdStat>> StatsAsync(DateOnly? from, DateOnly? to, CancellationToken ct = default);
    Task<IReadOnlyList<AdDay>> DailyAsync(long adId, DateOnly? from, DateOnly? to, CancellationToken ct = default);
    Task<int> PurgeAsync(int retentionDays, CancellationToken ct = default);
}

public sealed class AdRepository(IStoredProcedureExecutor db) : IAdRepository
{
    private static DateOnly D(DbDataReader r, string name) => DateOnly.FromDateTime(r.GetDateTime(r.GetOrdinal(name)));
    private static decimal M(DbDataReader r, string name) => r.GetDecimal(r.GetOrdinal(name));
    private static DateTime? Day(DateOnly? d) => d?.ToDateTime(TimeOnly.MinValue);
    private static DateTime Day(DateOnly d) => d.ToDateTime(TimeOnly.MinValue);

    private static AdPricing Pricing(DbDataReader r) => new(r.Long("id"), r.Str("position"), r.StrOrNull("country_code"), r.StrOrNull("city"), r.DecOrNull("daily_fee"), r.IntOrNull("max_ads"));
    private static Ad MapAd(DbDataReader r, bool withRunning) => new(r.Long("id"), r.Str("title"), r.Str("position"), r.Str("image_file_id"), r.Str("link_url"), r.StrOrNull("country_code"), r.StrOrNull("city"),
        r.Bool("is_active"), r.DecOrNull("fee_override"), r.Utc("created_at"), withRunning && r.Bool("running_today"));
    private static AdBatch Batch(DbDataReader r) => new(r.Long("id"), r.Long("ad_id"), D(r, "date_from"), D(r, "date_to"), M(r, "fee_per_day"), M(r, "total_fee"), r.Str("status"), r.Utc("created_at"), r.UtcOrNull("paid_at"));

    public Task<IReadOnlyList<AdPricing>> PricingAsync(CancellationToken ct = default) => db.QueryAsync("sp_ad_pricing_list", null, Pricing, ct);

    public async Task<AdPricing> SavePricingAsync(long actorId, string position, string? country, string? city, decimal? dailyFee, int? maxAds, CancellationToken ct = default) =>
        (await db.QuerySingleOrDefaultAsync("sp_ad_pricing_save", new ProcParams().Add("p_actor_id", actorId).Add("p_position", position).Add("p_country", country).Add("p_city", city)
            .Add("p_daily_fee", dailyFee).Add("p_max_ads", maxAds), Pricing, ct))!;

    public Task DeletePricingAsync(long actorId, long id, CancellationToken ct = default) =>
        db.ExecuteAsync("sp_ad_pricing_delete", new ProcParams().Add("p_actor_id", actorId).Add("p_id", id), ct);

    public async Task<ResolvedPricing> ResolveAsync(string position, string? country, string? city, CancellationToken ct = default) =>
        (await db.QueryAsync("sp_ad_pricing_resolve", new ProcParams().Add("p_position", position).Add("p_country", country).Add("p_city", city), r => new ResolvedPricing(M(r, "daily_fee"), r.Int("max_ads")), ct))[0];

    public async Task<Ad> SaveAdAsync(long actorId, long? id, string title, string position, string imageFileId, string linkUrl, string? country, string? city, bool? isActive, decimal? feeOverride, CancellationToken ct = default) =>
        (await db.QuerySingleOrDefaultAsync("sp_ads_save", new ProcParams().Add("p_actor_id", actorId).Add("p_id", id).Add("p_title", title).Add("p_position", position).Add("p_image_file_id", imageFileId)
            .Add("p_link_url", linkUrl).Add("p_country", country).Add("p_city", city).Add("p_is_active", isActive).Add("p_fee_override", feeOverride), r => MapAd(r, false), ct))!;

    public Task<IReadOnlyList<Ad>> AdsAsync(string? position, CancellationToken ct = default) =>
        db.QueryAsync("sp_ads_list", new ProcParams().Add("p_position", position), r => MapAd(r, true), ct);

    public Task<IReadOnlyList<CapacityDay>> CapacityAsync(long adId, DateOnly from, DateOnly to, CancellationToken ct = default) =>
        db.QueryAsync("sp_ad_capacity_days", new ProcParams().Add("p_ad_id", adId).Add("p_from", Day(from)).Add("p_to", Day(to)), r => new CapacityDay(D(r, "day"), r.Int("booked"), r.Int("max_ads")), ct);

    public async Task<AdBatch> CreateBatchAsync(long actorId, long adId, DateOnly from, DateOnly to, CancellationToken ct = default) =>
        (await db.QuerySingleOrDefaultAsync("sp_ad_batches_create", new ProcParams().Add("p_actor_id", actorId).Add("p_ad_id", adId).Add("p_from", Day(from)).Add("p_to", Day(to)), Batch, ct))!;

    public async Task<AdBatch> SetBatchStatusAsync(long actorId, long id, string status, CancellationToken ct = default) =>
        (await db.QuerySingleOrDefaultAsync("sp_ad_batches_set_status", new ProcParams().Add("p_actor_id", actorId).Add("p_id", id).Add("p_status", status), Batch, ct))!;

    public Task<IReadOnlyList<AdBatch>> BatchesAsync(long? adId, string? status, CancellationToken ct = default) =>
        db.QueryAsync("sp_ad_batches_list", new ProcParams().Add("p_ad_id", adId).Add("p_status", status), Batch, ct);

    public Task<IReadOnlyList<ServedAd>> ServeAsync(string position, string? country, string? city, int limit, CancellationToken ct = default) =>
        db.QueryAsync("sp_ads_serve", new ProcParams().Add("p_position", position).Add("p_country", country).Add("p_city", city).Add("p_limit", limit),
            r => new ServedAd(r.Long("id"), r.Str("title"), r.Str("position"), $"/api/files/{r.Str("image_file_id")}", $"/api/ads/{r.Long("id")}/click"), ct);

    public async Task<string> RecordAsync(long adId, string kind, string sessionHash, CancellationToken ct = default) =>
        (await db.QueryAsync("sp_ad_record", new ProcParams().Add("p_ad_id", adId).Add("p_kind", kind).Add("p_session", sessionHash), r => r.Str("link_url"), ct))[0];

    public Task<IReadOnlyList<AdStat>> StatsAsync(DateOnly? from, DateOnly? to, CancellationToken ct = default) =>
        db.QueryAsync("sp_ad_stats", new ProcParams().Add("p_from", Day(from)).Add("p_to", Day(to)),
            r => new AdStat(r.Long("ad_id"), r.Str("title"), r.Str("position"), r.Long("impressions"), r.Long("unique_impressions"), r.Long("clicks"), r.Long("unique_clicks")), ct);

    public Task<IReadOnlyList<AdDay>> DailyAsync(long adId, DateOnly? from, DateOnly? to, CancellationToken ct = default) =>
        db.QueryAsync("sp_ad_stats_daily", new ProcParams().Add("p_ad_id", adId).Add("p_from", Day(from)).Add("p_to", Day(to)),
            r => new AdDay(D(r, "day"), r.Long("impressions"), r.Long("unique_impressions"), r.Long("clicks"), r.Long("unique_clicks")), ct);

    public async Task<int> PurgeAsync(int retentionDays, CancellationToken ct = default) =>
        (await db.QueryAsync("sp_ad_events_purge", new ProcParams().Add("p_days", retentionDays), r => r.Int("deleted"), ct))[0];
}

/// <summary>Housekeeping: deletes raw impression/click records past the retention period. Daily totals are kept.</summary>
public sealed class AdPurgeJobHandler(IAdRepository ads, ISettingsService settings) : IJobHandler
{
    public const string JobType = "ads.purge";
    public string Type => JobType;
    public async Task HandleAsync(JobContext job, System.Text.Json.JsonElement payload, CancellationToken ct) => await ads.PurgeAsync(settings.GetInt("Ads.EventRetentionDays"), ct);
}
