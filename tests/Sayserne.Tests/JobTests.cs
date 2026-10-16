using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Sayserne.Api;
using Sayserne.Api.Jobs;
using Sayserne.Api.Platform;

namespace Sayserne.Tests;

public class JobQueueDatabaseTests
{
    private static async Task<(TestDatabase Db, JobQueue Queue)> NewAsync() { var db = await TestDatabase.CreateAsync(); return (db, new JobQueue(db.Executor())); }

    [Fact]
    public async Task A_new_job_is_pending_with_the_attempt_limit_from_settings_and_carries_its_payload()
    {
        var (db, q) = await NewAsync();
        try
        {
            var job = await q.EnqueueAsync("demo.work", new { fileId = "abc", n = 3 });
            Assert.Equal("pending", job.Status);
            Assert.Equal(0, job.Attempts);
            Assert.Equal(5, job.MaxAttempts);   // Jobs.MaxAttempts
            Assert.Equal(3, job.Payload!.Value.GetProperty("n").GetInt32());
            Assert.True((await db.JobRunInAsync(job.Id)) < 1);
        }
        finally { await db.DropAsync(); }
    }

    [Fact]
    public async Task The_same_dedupe_key_never_creates_a_second_job()
    {
        var (db, q) = await NewAsync();
        try
        {
            var a = await q.EnqueueAsync("demo.once", new { }, dedupeKey: "digest:2026-10-16");
            var b = await q.EnqueueAsync("demo.once", new { }, dedupeKey: "digest:2026-10-16");
            var other = await q.EnqueueAsync("demo.once", new { }, dedupeKey: "digest:2026-10-17");
            Assert.Equal(a.Id, b.Id);
            Assert.NotEqual(a.Id, other.Id);
            Assert.Equal(2, (await q.ListAsync(null, "demo.once", 1, 50)).Total);
            var n1 = await q.EnqueueAsync("demo.none", new { }); var n2 = await q.EnqueueAsync("demo.none", new { });   // no key: always a new job
            Assert.NotEqual(n1.Id, n2.Id);
        }
        finally { await db.DropAsync(); }
    }

    [Fact]
    public async Task Only_due_jobs_are_claimed_and_a_claim_marks_them_running_with_a_lease()
    {
        var (db, q) = await NewAsync();
        try
        {
            var due = await q.EnqueueAsync("demo.now", new { });
            var later = await q.EnqueueAsync("demo.later", new { }, runAtUtc: DateTime.UtcNow.AddMinutes(10));
            var claimed = await q.ClaimAsync("w1", 10, 300);
            Assert.Equal([due.Id], claimed.Select(j => j.Id).ToArray());
            Assert.Equal("running", claimed[0].Status);
            Assert.Equal(1, claimed[0].Attempts);
            Assert.Empty(await q.ClaimAsync("w2", 10, 300));                       // nothing else is due; the running job is leased
            await db.RunFixtureAsync("jobs_make_due.sql");
            Assert.Equal([later.Id], (await q.ClaimAsync("w2", 10, 300)).Select(j => j.Id).ToArray());
        }
        finally { await db.DropAsync(); }
    }

    [Fact]
    public async Task Many_workers_claiming_at_once_never_receive_the_same_job_twice()
    {
        var (db, q) = await NewAsync();
        try
        {
            for (var i = 0; i < 40; i++) await q.EnqueueAsync("demo.race", new { i });
            var gate = new TaskCompletionSource();
            var workers = Enumerable.Range(0, 10).Select(w => Task.Run(async () =>
            {
                await gate.Task;
                var mine = new List<long>();
                for (var round = 0; round < 6; round++) mine.AddRange((await q.ClaimAsync($"worker-{w}", 5, 300)).Select(j => j.Id));
                return mine;
            })).ToList();
            await Task.Delay(100); gate.SetResult();
            var all = (await Task.WhenAll(workers)).SelectMany(x => x).ToList();
            Assert.Equal(40, all.Count);
            Assert.Equal(40, all.Distinct().Count());   // each job went to exactly one worker
            Assert.Equal(0, (await q.ListAsync("pending", null, 1, 10)).Total);
        }
        finally { await db.DropAsync(); }
    }

    [Fact]
    public async Task A_job_whose_worker_died_is_taken_over_and_one_that_keeps_killing_workers_is_failed()
    {
        var (db, q) = await NewAsync();
        try
        {
            var job = await q.EnqueueAsync("demo.crash", new { });
            for (var attempt = 1; attempt <= 5; attempt++)
            {
                var got = await q.ClaimAsync($"w{attempt}", 1, 300);
                Assert.Equal(attempt, got.Single().Attempts);                      // the takeover counts as another attempt
                Assert.Empty(await q.ClaimAsync("bystander", 1, 300));             // a live lease is respected
                await db.RunFixtureAsync("jobs_expire_leases.sql");                // the worker "dies"
            }
            Assert.Empty(await q.ClaimAsync("w6", 1, 300));                        // attempts used up: no more takeovers...
            var final = (await q.ListAsync(null, "demo.crash", 1, 5)).Items.Single();
            Assert.Equal("failed", final.Status);                                  // ...it is parked as failed, with a reason
            Assert.Contains("worker stopped", final.LastError);
            Assert.Equal(job.Id, final.Id);
        }
        finally { await db.DropAsync(); }
    }

    [Fact]
    public async Task Only_the_worker_holding_the_lease_can_complete_or_fail_a_job()
    {
        var (db, q) = await NewAsync();
        try
        {
            var job = await q.EnqueueAsync("demo.owner", new { });
            await q.ClaimAsync("owner", 1, 300);
            await q.CompleteAsync(job.Id, "intruder");
            await q.FailAsync(job.Id, "intruder", "nope", permanent: true);
            Assert.Equal("running", (await q.ListAsync(null, "demo.owner", 1, 5)).Items.Single().Status);
            await q.CompleteAsync(job.Id, "owner");
            var done = (await q.ListAsync(null, "demo.owner", 1, 5)).Items.Single();
            Assert.Equal("done", done.Status);
            Assert.NotNull(done.FinishedAt);
            await q.FailAsync(job.Id, "owner", "too late", permanent: true);       // a finished job cannot be failed afterwards
            Assert.Equal("done", (await q.ListAsync(null, "demo.owner", 1, 5)).Items.Single().Status);
        }
        finally { await db.DropAsync(); }
    }

    [Fact]
    public async Task A_failing_job_is_retried_after_doubling_delays_then_parked_as_failed()
    {
        var (db, q) = await NewAsync();
        try
        {
            var job = await q.EnqueueAsync("demo.flaky", new { });
            var expected = new[] { 30, 60, 120, 240 };                              // Jobs.RetryDelaySeconds = 30, doubling
            for (var attempt = 1; attempt <= 4; attempt++)
            {
                Assert.Equal(attempt, (await q.ClaimAsync("w", 1, 300)).Single().Attempts);
                await q.FailAsync(job.Id, "w", $"boom {attempt}", permanent: false);
                var row = (await q.ListAsync(null, "demo.flaky", 1, 5)).Items.Single();
                Assert.Equal("pending", row.Status);
                Assert.Equal($"boom {attempt}", row.LastError);
                var wait = await db.JobRunInAsync(job.Id);
                Assert.True(wait > expected[attempt - 1] - 5 && wait <= expected[attempt - 1] + 1, $"attempt {attempt}: next run in {wait}s, expected about {expected[attempt - 1]}s");
                Assert.Empty(await q.ClaimAsync("w", 1, 300));                      // not due yet
                await db.RunFixtureAsync("jobs_make_due.sql");
            }
            Assert.Equal(5, (await q.ClaimAsync("w", 1, 300)).Single().Attempts);
            await q.FailAsync(job.Id, "w", "boom 5", permanent: false);              // the fifth failure is the last
            var failed = (await q.ListAsync(null, "demo.flaky", 1, 5)).Items.Single();
            Assert.Equal("failed", failed.Status);
            Assert.Equal("boom 5", failed.LastError);
        }
        finally { await db.DropAsync(); }
    }

    [Fact]
    public async Task A_permanent_failure_is_not_retried_and_the_error_is_truncated_safely()
    {
        var (db, q) = await NewAsync();
        try
        {
            var job = await q.EnqueueAsync("demo.perm", new { });
            await q.ClaimAsync("w", 1, 300);
            await q.FailAsync(job.Id, "w", new string('x', 5000), permanent: true);
            var row = (await q.ListAsync(null, "demo.perm", 1, 5)).Items.Single();
            Assert.Equal("failed", row.Status);
            Assert.Equal(1, row.Attempts);
            Assert.Equal(1000, row.LastError!.Length);
        }
        finally { await db.DropAsync(); }
    }

    [Fact]
    public async Task Lists_and_stats_can_be_filtered_and_paged()
    {
        var (db, q) = await NewAsync();
        try
        {
            for (var i = 0; i < 5; i++) await q.EnqueueAsync("demo.a", new { });
            for (var i = 0; i < 3; i++) await q.EnqueueAsync("demo.b", new { });
            var claimed = await q.ClaimAsync("w", 2, 300);
            await q.CompleteAsync(claimed[0].Id, "w"); await q.FailAsync(claimed[1].Id, "w", "x", permanent: true);
            Assert.Equal(8, (await q.ListAsync(null, null, 1, 3)).Total);
            Assert.Equal(3, (await q.ListAsync(null, null, 1, 3)).Items.Count);
            Assert.Equal(6, (await q.ListAsync("pending", null, 1, 50)).Total);
            Assert.Equal(3, (await q.ListAsync(null, "demo.b", 1, 50)).Total);
            var stats = await q.StatsAsync();
            Assert.Equal(8, stats.Sum(s => s.Count));
            Assert.Equal(1, stats.Single(s => s.Status == "done").Count);
            Assert.Equal(1, stats.Single(s => s.Status == "failed").Count);
        }
        finally { await db.DropAsync(); }
    }

    [Fact]
    public async Task An_admin_retry_revives_only_failed_jobs_and_is_audited()
    {
        var (db, q) = await NewAsync();
        try
        {
            var users = new Sayserne.Api.Data.UserRepository(db.Executor());
            var admin = await users.CreateLocalAsync(ApiHost.UniqueEmail("ops"), "hash", "Ops", "Admin");
            var job = await q.EnqueueAsync("demo.retry", new { });
            Assert.Equal("JOB_NOT_RETRYABLE", (await Assert.ThrowsAsync<AppException>(() => q.RetryAsync(admin.Id, job.Id))).Code);   // pending
            await q.ClaimAsync("w", 1, 300); await q.FailAsync(job.Id, "w", "bad", permanent: true);
            var revived = await q.RetryAsync(admin.Id, job.Id);
            Assert.Equal("pending", revived.Status);
            Assert.Equal(0, revived.Attempts);
            Assert.Null(revived.LastError);
            Assert.Equal("JOB_NOT_RETRYABLE", (await Assert.ThrowsAsync<AppException>(() => q.RetryAsync(admin.Id, 999999))).Code);
            var audit = await new PlatformRepository(db.Executor()).AuditAsync(new AuditFilter(null, null, admin.Id, "jobs.retry", null, null, 1, 5));
            Assert.Equal(1, audit.Total);
        }
        finally { await db.DropAsync(); }
    }
}

public class JobRunnerTests
{
    private sealed class Probe(string type, Func<JobContext, Task> body) : IJobHandler
    {
        public List<(int Attempt, int Max)> Calls { get; } = [];
        public string Type => type;
        public async Task HandleAsync(JobContext job, JsonElement payload, CancellationToken ct) { lock (Calls) Calls.Add((job.Attempt, job.MaxAttempts)); await body(job); }
    }

    [Fact]
    public async Task The_runner_runs_due_jobs_marks_them_done_and_retries_failures_until_they_succeed()
    {
        var ok = new Probe("test.ok", _ => Task.CompletedTask);
        var flaky = new Probe("test.flaky", job => job.Attempt < 3 ? throw new InvalidOperationException("not yet") : Task.CompletedTask);
        var fatal = new Probe("test.fatal", _ => throw new PermanentJobException("never going to work"));
        await using var iso = await IsolatedHost.StartAsync(services: s => { s.AddSingleton<IJobHandler>(ok); s.AddSingleton<IJobHandler>(flaky); s.AddSingleton<IJobHandler>(fatal); });
        var runner = iso.Host.App.Services.GetRequiredService<JobRunner>();
        var queue = iso.Host.App.Services.GetRequiredService<IJobQueue>();
        var okJob = await queue.EnqueueAsync("test.ok", new { }); var flakyJob = await queue.EnqueueAsync("test.flaky", new { });
        var fatalJob = await queue.EnqueueAsync("test.fatal", new { }); var orphan = await queue.EnqueueAsync("test.nobody-handles-this", new { });

        var ran = 0; int cycle;
        while ((cycle = await runner.RunOnceAsync()) > 0) ran += cycle;   // at most Jobs:Workers (2) jobs per cycle
        Assert.Equal(4, ran);
        async Task<JobRow> Row(long id) => (await queue.ListAsync(null, null, 1, 50)).Items.Single(j => j.Id == id);
        Assert.Equal("done", (await Row(okJob.Id)).Status);
        Assert.Equal("pending", (await Row(flakyJob.Id)).Status);
        Assert.Equal("not yet", (await Row(flakyJob.Id)).LastError);
        var f = await Row(fatalJob.Id);
        Assert.Equal("failed", f.Status); Assert.Equal("never going to work", f.LastError);
        var o = await Row(orphan.Id);
        Assert.Equal("failed", o.Status); Assert.Contains("No handler", o.LastError);

        Assert.Equal(0, await runner.RunOnceAsync());                       // the flaky job is waiting out its delay
        await iso.Db.RunFixtureAsync("jobs_make_due.sql"); Assert.Equal(1, await runner.RunOnceAsync());
        Assert.Equal("pending", (await Row(flakyJob.Id)).Status);
        await iso.Db.RunFixtureAsync("jobs_make_due.sql"); Assert.Equal(1, await runner.RunOnceAsync());
        Assert.Equal("done", (await Row(flakyJob.Id)).Status);               // third attempt succeeded
        Assert.Equal([(1, 5), (2, 5), (3, 5)], flaky.Calls);
        Assert.Single(ok.Calls);
        Assert.Single(fatal.Calls);                                         // permanent: tried once
    }

    [Fact]
    public async Task Several_API_instances_running_the_same_queue_do_each_job_exactly_once()
    {
        var counter = new Probe("test.count", _ => Task.Delay(20));
        await using var iso = await IsolatedHost.StartAsync(services: s => s.AddSingleton<IJobHandler>(counter));
        await using var second = await ApiHost.StartAsync(database: iso.Db, services: s => s.AddSingleton<IJobHandler>(counter));
        var queue = iso.Host.App.Services.GetRequiredService<IJobQueue>();
        for (var i = 0; i < 30; i++) await queue.EnqueueAsync("test.count", new { i });
        var r1 = iso.Host.App.Services.GetRequiredService<JobRunner>(); var r2 = second.App.Services.GetRequiredService<JobRunner>();
        var total = 0;
        await Task.WhenAll(Task.Run(async () => { for (var i = 0; i < 20; i++) Interlocked.Add(ref total, await r1.RunOnceAsync()); }), Task.Run(async () => { for (var i = 0; i < 20; i++) Interlocked.Add(ref total, await r2.RunOnceAsync()); }));
        Assert.Equal(30, total);
        Assert.Equal(30, counter.Calls.Count);
        Assert.Equal(30, (await queue.ListAsync("done", "test.count", 1, 100)).Total);
    }

    [Fact]
    public async Task Staff_see_and_retry_jobs_only_with_the_matching_permission()
    {
        var fatal = new Probe("test.fatal2", _ => throw new PermanentJobException("broken"));
        await using var iso = await IsolatedHost.StartAsync(services: s => s.AddSingleton<IJobHandler>(fatal));
        var host = iso.Host;
        var queue = host.App.Services.GetRequiredService<IJobQueue>();
        var runner = host.App.Services.GetRequiredService<JobRunner>();
        var job = await queue.EnqueueAsync("test.fatal2", new { }); await runner.RunOnceAsync();

        Assert.Equal(HttpStatusCode.Unauthorized, (await host.NewClient().GetAsync("/api/admin/jobs")).Status);
        var (nobody, _, _) = await host.CreateStaffAsync("users.read");
        Assert.Equal(HttpStatusCode.Forbidden, (await nobody.GetAsync("/api/admin/jobs")).Status);
        var (reader, _, _) = await host.CreateStaffAsync("jobs.read");
        var (s1, list) = await reader.GetAsync("/api/admin/jobs?status=failed");
        Assert.Equal(HttpStatusCode.OK, s1);
        Assert.Equal("broken", list.GetProperty("items").EnumerateArray().Single(j => j.GetProperty("id").GetInt64() == job.Id).GetProperty("lastError").GetString());
        Assert.Equal(HttpStatusCode.OK, (await reader.GetAsync("/api/admin/jobs/stats")).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.PostAsync($"/api/admin/jobs/{job.Id}/retry")).Status);   // read is not manage

        var (manager, _, _) = await host.CreateStaffAsync("jobs.manage");
        var (s2, retried) = await manager.PostAsync($"/api/admin/jobs/{job.Id}/retry");
        Assert.Equal(HttpStatusCode.OK, s2);
        Assert.Equal("pending", retried.GetProperty("status").GetString());
        var (s3, again) = await manager.PostAsync($"/api/admin/jobs/{job.Id}/retry");   // now pending, not failed
        Assert.Equal(HttpStatusCode.Conflict, s3);
        Assert.Equal("JOB_NOT_RETRYABLE", TestClient.Error(again));
    }
}
