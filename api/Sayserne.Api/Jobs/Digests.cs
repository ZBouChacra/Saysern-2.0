using System.Text.Json;
using Sayserne.Api.Data;
using Sayserne.Api.Email;
using Sayserne.Api.Platform;

namespace Sayserne.Api.Jobs;

public sealed record DueAlert(long Id, int Days);
public sealed record DigestAlert(long Id, long UserId, string Name, SearchFilters Criteria, DateTime Since, bool UserLocked);

public interface IDigestRepository
{
    Task<IReadOnlyList<DueAlert>> DueAsync(int limit, CancellationToken ct = default);
    Task<DigestAlert?> GetAsync(long alertId, CancellationToken ct = default);
    Task MarkAsync(long alertId, CancellationToken ct = default);
    Task<IReadOnlyList<long>> CreatedSinceAsync(IReadOnlyCollection<long> professionIds, DateTime sinceUtc, CancellationToken ct = default);
}

public sealed class DigestRepository(IStoredProcedureExecutor db) : IDigestRepository
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public Task<IReadOnlyList<DueAlert>> DueAsync(int limit, CancellationToken ct = default) =>
        db.QueryAsync("sp_alerts_due", new ProcParams().Add("p_limit", limit), r => new DueAlert(r.Long("id"), r.Int("days")), ct);

    public Task<DigestAlert?> GetAsync(long alertId, CancellationToken ct = default) =>
        db.QuerySingleOrDefaultAsync("sp_alerts_digest_get", new ProcParams().Add("p_id", alertId),
            r => new DigestAlert(r.Long("id"), r.Long("user_id"), r.Str("name"), JsonSerializer.Deserialize<SearchFilters>(r.Str("search_criteria"), Json) ?? new SearchFilters(), r.Utc("since"), r.Bool("is_locked")), ct);

    public Task MarkAsync(long alertId, CancellationToken ct = default) => db.ExecuteAsync("sp_alerts_digest_mark", new ProcParams().Add("p_id", alertId), ct);

    public async Task<IReadOnlyList<long>> CreatedSinceAsync(IReadOnlyCollection<long> professionIds, DateTime sinceUtc, CancellationToken ct = default) =>
        professionIds.Count == 0 ? [] : await db.QueryAsync("sp_professions_created_since", new ProcParams().Add("p_ids", JsonSerializer.Serialize(professionIds)).Add("p_since", sinceUtc), r => r.Long("id"), ct);
}

/// <summary>Decides which saved searches are due and queues one digest job for each (once per search per period, however often this runs).</summary>
public sealed class DigestScheduler(IDigestRepository digests, IJobQueue jobs, TimeProvider clock, IConfiguration config, ILogger<DigestScheduler> log) : BackgroundService
{
    public async Task<int> RunOnceAsync(CancellationToken ct = default)
    {
        var queued = 0;
        var now = clock.GetUtcNow();
        await jobs.EnqueueAsync(Ads.AdPurgeJobHandler.JobType, null, dedupeKey: $"ads.purge:{now:yyyy-MM-dd}", ct: ct);   // once a day, however often this runs
        foreach (var due in await digests.DueAsync(500, ct))
        {
            var period = now.ToUnixTimeSeconds() / (due.Days * 86400L);
            await jobs.EnqueueAsync(AlertDigestJobHandler.JobType, new { alertId = due.Id }, dedupeKey: $"digest:{due.Id}:{period}", ct: ct);
            queued++;
        }
        return queued;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var every = TimeSpan.FromMinutes(Math.Max(config.GetValue<int?>("Jobs:SchedulerMinutes") ?? 15, 1));
        using var timer = new PeriodicTimer(every);
        try
        {
            do
            {
                try { await RunOnceAsync(stoppingToken); }
                catch (Exception ex) when (ex is not OperationCanceledException) { log.LogWarning(ex, "The digest scheduler could not reach the database; will try again."); }
            } while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) { /* shutting down */ }
    }
}

/// <summary>E-mails a saved search's new professionals (those who joined since the last digest). Nothing new means no e-mail.</summary>
public sealed class AlertDigestJobHandler(IDigestRepository digests, IProfessionalRepository professionals, INotifier notifier, AccountEmails links, ISettingsService settings) : IJobHandler
{
    public const string JobType = "alerts.digest";
    public string Type => JobType;

    public async Task HandleAsync(JobContext job, JsonElement payload, CancellationToken ct)
    {
        var alert = await digests.GetAsync(payload.GetProperty("alertId").GetInt64(), ct);
        if (alert is null || alert.UserLocked) return;

        var found = await professionals.SearchAsync(alert.Criteria with { Page = 1, PageSize = settings.GetInt("Search.MaxPageSize") });
        var newIds = (await digests.CreatedSinceAsync(found.Items.Select(l => l.ProfessionId).ToList(), alert.Since, ct)).ToHashSet();
        var fresh = found.Items.Where(l => newIds.Contains(l.ProfessionId)).ToList();
        if (fresh.Count > 0)
        {
            var shown = fresh.Take(settings.GetInt("Alerts.DigestMaxListings"));
            var list = string.Join("\n", shown.Select(l => $"• {l.FirstName} {l.LastName} — {l.ServiceName}{(string.IsNullOrWhiteSpace(l.City) ? "" : ", " + l.City)}"));
            await notifier.NotifyAsync("alert_digest", alert.UserId, new Dictionary<string, string>
            {
                ["alert"] = alert.Name, ["count"] = fresh.Count.ToString(), ["list"] = list, ["link"] = links.Link("/alerts"),
            }, ct);
        }
        await digests.MarkAsync(alert.Id, ct);   // the next digest covers only what arrives from now on
    }
}
