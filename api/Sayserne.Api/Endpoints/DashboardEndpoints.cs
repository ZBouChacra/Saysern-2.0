using Sayserne.Api.Auth;
using Sayserne.Api.Data;
using Sayserne.Api.Platform;

namespace Sayserne.Api.Endpoints;

/// <summary>The back-office home page: headline numbers, each group shown only to staff whose role lets them see that area, plus recent activity.</summary>
public static class DashboardEndpoints
{
    public static void MapDashboard(this IEndpointRouteBuilder app) =>
        app.MapGet("/api/admin/dashboard", async (HttpContext ctx, IStoredProcedureExecutor db, IPlatformRepository platform) =>
        {
            bool Can(string p) => ctx.User.HasClaim(AdminAuth.PermissionClaim, p);
            var row = (await db.QueryAsync("sp_dashboard_stats", null, r => Enumerable.Range(0, r.FieldCount).ToDictionary(i => r.GetName(i), i => r.GetValue(i) is DBNull ? 0m : Convert.ToDecimal(r.GetValue(i))), ctx.RequestAborted))[0];
            long N(string c) => (long)row[c];
            var result = new Dictionary<string, object>();
            if (Can("users.read")) result["users"] = new { total = N("users_total"), last7Days = N("users_7d"), last30Days = N("users_30d"), professionals = N("professionals"), listings = N("listings"), locked = N("users_locked"),
                appointments = new { pending = N("appointments_pending"), approved = N("appointments_approved"), completed = N("appointments_completed"), cancelled = N("appointments_cancelled") } };
            if (Can("reviews.read")) result["reviews"] = new { total = N("reviews_total"), hidden = N("reviews_hidden"), average = row["reviews_avg"] };
            if (Can("contact.read")) result["contact"] = new { unanswered = N("contact_new"), total = N("contact_total") };
            if (Can("ads.read")) result["ads"] = new { runningToday = N("ads_running"), pendingBatches = N("ad_batches_pending") };
            if (Can("premium.read")) result["premium"] = new { activeToday = N("premium_today"), pendingBatches = N("premium_pending") };
            if (Can("payments.read")) result["payments"] = new { receiptsLast30Days = N("receipts_30d"), receivedLast30Days = row["received_30d"], receivedThisMonth = row["received_month"], voidLast30Days = N("receipts_void_30d"), currency = "USD" };
            if (Can("jobs.read") || Can("email.read")) result["system"] = new { failedJobs = Can("jobs.read") ? N("jobs_failed") : (long?)null, pendingJobs = Can("jobs.read") ? N("jobs_pending") : (long?)null,
                failedEmails = Can("email.read") ? N("emails_failed") : (long?)null, queuedEmails = Can("email.read") ? N("emails_queued") : (long?)null };
            if (Can("audit.read")) result["recentActivity"] = (await platform.AuditAsync(new AuditFilter(null, null, null, null, null, null, 1, 8), ctx.RequestAborted)).Items
                .Select(a => new { a.Id, a.OccurredAt, a.ActorName, a.Action, a.Summary });
            return Results.Ok(result);
        }).RequireAuthorization(PermissionExtensions.StaffPolicy);
}
