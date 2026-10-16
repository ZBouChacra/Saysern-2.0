using System.Text.Json;
using Sayserne.Api.Data;
using Sayserne.Api.Media;
using Sayserne.Api.Platform;

namespace Sayserne.Api.Jobs;

public sealed record JobRow(long Id, string Type, JsonElement? Payload, string Status, int Attempts, int MaxAttempts, DateTime RunAt, string? LastError, DateTime CreatedAt, DateTime? FinishedAt);
public sealed record JobStat(string Type, string Status, int Count);

public interface IJobQueue
{
    Task<JobRow> EnqueueAsync(string type, object? payload, DateTime? runAtUtc = null, string? dedupeKey = null, CancellationToken ct = default);
    Task<IReadOnlyList<JobRow>> ClaimAsync(string worker, int limit, int leaseSeconds, CancellationToken ct = default);
    Task CompleteAsync(long id, string worker, CancellationToken ct = default);
    Task FailAsync(long id, string worker, string error, bool permanent, CancellationToken ct = default);
    Task<PagedResult<JobRow>> ListAsync(string? status, string? type, int page, int pageSize, CancellationToken ct = default);
    Task<IReadOnlyList<JobStat>> StatsAsync(CancellationToken ct = default);
    Task<JobRow> RetryAsync(long actorId, long id, CancellationToken ct = default);
}

public sealed class JobQueue(IStoredProcedureExecutor db) : IJobQueue
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static JobRow Map(System.Data.Common.DbDataReader r)
    {
        var payload = r.StrOrNull("payload");
        return new JobRow(r.Long("id"), r.Str("job_type"), payload is null ? null : JsonDocument.Parse(payload).RootElement.Clone(), r.Str("status"), r.Int("attempts"), r.Int("max_attempts"),
            r.Utc("run_at"), r.StrOrNull("last_error"), r.Utc("created_at"), r.UtcOrNull("finished_at"));
    }

    public async Task<JobRow> EnqueueAsync(string type, object? payload, DateTime? runAtUtc = null, string? dedupeKey = null, CancellationToken ct = default) =>
        (await db.QuerySingleOrDefaultAsync("sp_jobs_enqueue", new ProcParams().Add("p_type", type).Add("p_payload", payload is null ? null : JsonSerializer.Serialize(payload, Json))
            .Add("p_run_at", runAtUtc).Add("p_dedupe_key", dedupeKey), Map, ct))!;

    public Task<IReadOnlyList<JobRow>> ClaimAsync(string worker, int limit, int leaseSeconds, CancellationToken ct = default) =>
        db.QueryAsync("sp_jobs_claim", new ProcParams().Add("p_worker", worker).Add("p_limit", limit).Add("p_lease_seconds", leaseSeconds), Map, ct);

    public Task CompleteAsync(long id, string worker, CancellationToken ct = default) =>
        db.ExecuteAsync("sp_jobs_complete", new ProcParams().Add("p_id", id).Add("p_worker", worker), ct);

    public Task FailAsync(long id, string worker, string error, bool permanent, CancellationToken ct = default) =>
        db.ExecuteAsync("sp_jobs_fail", new ProcParams().Add("p_id", id).Add("p_worker", worker).Add("p_error", error).Add("p_permanent", permanent), ct);

    public async Task<PagedResult<JobRow>> ListAsync(string? status, string? type, int page, int pageSize, CancellationToken ct = default)
    {
        var (rows, totals) = await db.QueryTwoAsync("sp_jobs_list", new ProcParams().Add("p_status", status).Add("p_type", type).Add("p_page", page).Add("p_page_size", pageSize), Map, r => r.Long("total"), ct);
        return new PagedResult<JobRow>(rows, totals.Count > 0 ? totals[0] : 0, Math.Max(page, 1), Math.Clamp(pageSize, 1, 200));
    }

    public Task<IReadOnlyList<JobStat>> StatsAsync(CancellationToken ct = default) =>
        db.QueryAsync("sp_jobs_stats", null, r => new JobStat(r.Str("job_type"), r.Str("status"), r.Int("cnt")), ct);

    public async Task<JobRow> RetryAsync(long actorId, long id, CancellationToken ct = default) =>
        (await db.QuerySingleOrDefaultAsync("sp_jobs_retry", new ProcParams().Add("p_actor_id", actorId).Add("p_id", id), Map, ct))!;
}

public sealed record JobContext(long JobId, int Attempt, int MaxAttempts) { public bool IsLastAttempt => Attempt >= MaxAttempts; }

/// <summary>One kind of background work. Throw <see cref="PermanentJobException"/> when retrying cannot help; any other exception is retried with growing delays.</summary>
public interface IJobHandler
{
    string Type { get; }
    Task HandleAsync(JobContext job, JsonElement payload, CancellationToken ct);
}

public sealed class PermanentJobException(string message) : Exception(message);

/// <summary>Claims due jobs and runs them. Several API instances may run it at once: the database hands each job to exactly one worker.</summary>
public sealed class JobRunner(IJobQueue queue, IEnumerable<IJobHandler> handlers, ISettingsService settings, IConfiguration config, ILogger<JobRunner> log) : BackgroundService
{
    private readonly Dictionary<string, IJobHandler> _handlers = handlers.ToDictionary(h => h.Type, StringComparer.Ordinal);
    private readonly string _worker = WorkerName();
    /// <summary>Unique per runner (machine, process and a random part), at most 100 characters (the column width).</summary>
    private static string WorkerName()
    {
        var unique = $":{Environment.ProcessId}:{Guid.NewGuid():N}";
        var machine = Environment.MachineName.Length > 100 - unique.Length ? Environment.MachineName[..(100 - unique.Length)] : Environment.MachineName;
        return machine + unique;
    }

    /// <summary>Runs whatever is due right now, once. The background loop calls this repeatedly; tests call it directly.</summary>
    public async Task<int> RunOnceAsync(CancellationToken ct = default)
    {
        var batch = Math.Max(config.GetValue<int?>("Jobs:Workers") ?? 2, 1);
        var jobs = await queue.ClaimAsync(_worker, batch, settings.GetInt("Jobs.LeaseSeconds"), ct);
        await Task.WhenAll(jobs.Select(j => RunJobAsync(j, ct)));
        return jobs.Count;
    }

    private async Task RunJobAsync(JobRow job, CancellationToken ct)
    {
        try
        {
            if (!_handlers.TryGetValue(job.Type, out var handler)) throw new PermanentJobException($"No handler is registered for '{job.Type}'.");
            await handler.HandleAsync(new JobContext(job.Id, job.Attempts, job.MaxAttempts), job.Payload ?? default, ct);
            await queue.CompleteAsync(job.Id, _worker, CancellationToken.None);
        }
        catch (PermanentJobException ex) { await queue.FailAsync(job.Id, _worker, ex.Message, permanent: true, CancellationToken.None); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { /* shutting down: the lease expires and another worker takes it */ }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Job {JobId} ({Type}) failed on attempt {Attempt}.", job.Id, job.Type, job.Attempts);
            await queue.FailAsync(job.Id, _worker, ex.Message, permanent: false, CancellationToken.None);
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var poll = TimeSpan.FromSeconds(Math.Max(config.GetValue<int?>("Jobs:PollSeconds") ?? 2, 1));
        while (!stoppingToken.IsCancellationRequested)
        {
            var worked = 0;
            try { worked = await RunOnceAsync(stoppingToken); }
            catch (Exception ex) when (ex is not OperationCanceledException) { log.LogError(ex, "The job runner could not reach the database; will retry."); }
            if (worked == 0) { try { await Task.Delay(poll, stoppingToken); } catch (OperationCanceledException) { break; } }
        }
    }
}

/// <summary>Compresses an uploaded video (or image) in the background and publishes the result.</summary>
public sealed class MediaProcessJobHandler(IFileRepository files, MediaIngestService ingest) : IJobHandler
{
    public const string JobType = "media.process";
    public string Type => JobType;

    public async Task HandleAsync(JobContext job, JsonElement payload, CancellationToken ct)
    {
        var id = payload.GetProperty("fileId").GetString()!;
        var file = await files.GetAsync(id, ct);
        if (file is null || file.Status != "processing") return;   // already handled (e.g. a duplicate delivery)
        try { await ingest.ProcessAsync(file, ct); }
        catch (MediaException ex)
        {
            await files.MarkFailedAsync(id, ex.Code, CancellationToken.None);
            await ingest.DiscardIncomingAsync(file);
            throw new PermanentJobException(ex.Code);
        }
        catch (Exception) when (job.IsLastAttempt)
        {
            await files.MarkFailedAsync(id, "PROCESSING_FAILED", CancellationToken.None);
            throw;
        }
    }
}
