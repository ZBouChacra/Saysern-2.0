using Sayserne.Api.Data;
using Sayserne.Api.Email;
using Sayserne.Api.Platform;

namespace Sayserne.Api.Jobs;

public sealed record AppointmentJobRow(long Id, long ClientId, long ProfessionalId, DateTime StartsAt, string ClientFirstName, string ClientLastName, string ProfessionalFirstName, string ProfessionalLastName);

public interface IAppointmentMaintenanceRepository
{
    Task<IReadOnlyList<AppointmentJobRow>> ExpirePendingAsync(int limit, CancellationToken ct = default);
    Task<IReadOnlyList<AppointmentJobRow>> RemindersDueAsync(int hours, int limit, CancellationToken ct = default);
    Task<bool> MarkRemindedAsync(long id, CancellationToken ct = default);
}

public sealed class AppointmentMaintenanceRepository(IStoredProcedureExecutor db) : IAppointmentMaintenanceRepository
{
    private static AppointmentJobRow Row(System.Data.Common.DbDataReader r) => new(r.Long("id"), r.Long("client_id"), r.Long("professional_id"), r.Utc("starts_at"),
        r.Str("client_first_name"), r.Str("client_last_name"), r.Str("professional_first_name"), r.Str("professional_last_name"));

    public Task<IReadOnlyList<AppointmentJobRow>> ExpirePendingAsync(int limit, CancellationToken ct = default) => db.QueryAsync("sp_appointments_expire_pending", new ProcParams().Add("p_limit", limit), Row, ct);
    public Task<IReadOnlyList<AppointmentJobRow>> RemindersDueAsync(int hours, int limit, CancellationToken ct = default) => db.QueryAsync("sp_appointments_reminders_due", new ProcParams().Add("p_hours", hours).Add("p_limit", limit), Row, ct);
    public async Task<bool> MarkRemindedAsync(long id, CancellationToken ct = default) => (await db.QueryAsync("sp_appointments_mark_reminded", new ProcParams().Add("p_id", id), r => r.Long("marked"), ct))[0] == 1;
}

/// <summary>
/// Runs every few minutes (queued by the scheduler). A booking request nobody answered by its start time is cancelled and the client told; an approved appointment
/// that starts within <c>Booking.ReminderHours</c> reminds both people once. Several servers may run this at once: each row is claimed by one of them.
/// </summary>
public sealed class AppointmentMaintenanceJobHandler(IAppointmentMaintenanceRepository repo, MarketplaceNotifier notify, ISettingsService settings) : IJobHandler
{
    public const string JobType = "appointments.maintenance";
    public string Type => JobType;

    public async Task HandleAsync(JobContext job, System.Text.Json.JsonElement payload, CancellationToken ct)
    {
        if (settings.GetBool("Booking.ExpirePending"))
            foreach (var a in await repo.ExpirePendingAsync(200, ct)) await notify.BookingExpired(a);
        var hours = settings.GetInt("Booking.ReminderHours");
        if (hours > 0)
            foreach (var a in await repo.RemindersDueAsync(hours, 200, ct))
                if (await repo.MarkRemindedAsync(a.Id, ct)) await notify.BookingReminder(a);
    }
}
