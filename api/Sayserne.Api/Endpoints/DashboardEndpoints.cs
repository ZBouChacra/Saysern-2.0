using Sayserne.Api.Auth;
using Sayserne.Api.Data;
using Sayserne.Api.Platform;

namespace Sayserne.Api.Endpoints;

/// <summary>The back-office home page: headline numbers, each group shown only to staff whose role lets them see that area, plus recent activity.</summary>
public static class DashboardEndpoints
{
    private sealed record MonthRow(int Month, long NewUsers, long Appointments, long Reviews, long ContactMessages, long Receipts, decimal Received, long Voided);

    /// <summary>The year asked for: a whole number from 2000 up to next year; empty means this year.</summary>
    private static int ParseYear(string? text, TimeProvider clock)
    {
        var now = clock.GetUtcNow().Year;
        if (string.IsNullOrWhiteSpace(text)) return now;
        if (!int.TryParse(text, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var year) || year < 2000 || year > now + 1)
            throw new AppException("VALIDATION_FAILED", 400, fieldErrors: new Dictionary<string, string> { ["year"] = "Enter a year from 2000 to next year." });
        return year;
    }

    private static async Task<(List<MonthRow> Months, List<int> Years)> LoadYear(IStoredProcedureExecutor db, int year, CancellationToken ct)
    {
        var (months, years) = await db.QueryTwoAsync("sp_dashboard_year", new ProcParams().Add("p_year", year),
            r => new MonthRow(r.Int("month"), r.Long("new_users"), r.Long("appointments"), r.Long("reviews"), r.Long("contact_messages"), r.Long("receipts"), r.Dec("received"), r.Long("voided")),
            r => r.Int("year"), ct);
        return (months.ToList(), years.ToList());
    }

    public static void MapDashboard(this IEndpointRouteBuilder app)
    {
        // the same numbers, month by month, for one calendar year; each column only for staff who may see that area
        app.MapGet("/api/admin/dashboard/year", async (HttpContext ctx, IStoredProcedureExecutor db, TimeProvider clock, string? year) =>
        {
            var y = ParseYear(year, clock);
            var (months, years) = await LoadYear(db, y, ctx.RequestAborted);
            var cols = Columns(ctx);
            return Results.Ok(new { year = y, years, columns = cols.Select(c => c.Key), months = months.Select(m => { var row = new Dictionary<string, object> { ["month"] = m.Month }; foreach (var c in cols) row[c.Key] = c.Value(m); return row; }) });
        }).RequireAuthorization(PermissionExtensions.StaffPolicy);

        // a spreadsheet of that year (UTF-8 with a byte-order mark so Excel reads it correctly)
        app.MapGet("/api/admin/dashboard/year.csv", async (HttpContext ctx, IStoredProcedureExecutor db, TimeProvider clock, string? year) =>
        {
            var y = ParseYear(year, clock);
            var (months, _) = await LoadYear(db, y, ctx.RequestAborted);
            var cols = Columns(ctx);
            var csv = new System.Text.StringBuilder("\uFEFF");
            csv.Append("Year,Month").Append(string.Concat(cols.Select(c => "," + c.Header))).Append("\r\n");
            foreach (var m in months)
                csv.Append(y).Append(',').Append(System.Globalization.CultureInfo.InvariantCulture.DateTimeFormat.GetMonthName(m.Month)).Append(string.Concat(cols.Select(c => "," + Convert.ToString(c.Value(m), System.Globalization.CultureInfo.InvariantCulture)))).Append("\r\n");
            ctx.Response.Headers.ContentDisposition = $"attachment; filename=\"sayserne-{y}.csv\"";
            return Results.Text(csv.ToString(), "text/csv; charset=utf-8");
        }).RequireAuthorization(PermissionExtensions.StaffPolicy);

        MapSummary(app);
    }

    private static List<(string Key, string Header, Func<MonthRow, object> Value)> Columns(HttpContext ctx)
    {
        bool Can(string p) => ctx.User.HasClaim(AdminAuth.PermissionClaim, p);
        var cols = new List<(string, string, Func<MonthRow, object>)>();
        if (Can("users.read")) { cols.Add(("newUsers", "New users", m => m.NewUsers)); cols.Add(("appointments", "Appointments requested", m => m.Appointments)); }
        if (Can("reviews.read")) cols.Add(("reviews", "Reviews", m => m.Reviews));
        if (Can("contact.read")) cols.Add(("contactMessages", "Contact messages", m => m.ContactMessages));
        if (Can("payments.read")) { cols.Add(("receipts", "Receipts issued", m => m.Receipts)); cols.Add(("received", "Amount received (USD)", m => m.Received)); cols.Add(("voided", "Receipts voided", m => m.Voided)); }
        return cols;
    }

    private static void MapSummary(IEndpointRouteBuilder app) =>
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
