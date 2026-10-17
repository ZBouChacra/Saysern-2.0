using System.Globalization;

namespace Sayserne.Api.Data;

/// <summary>Is a requested appointment inside the professional's weekly working hours? Pure logic, judged in the professional's own time zone.</summary>
public static class AvailabilityRules
{
    /// <param name="slots">The professional's weekly windows (day 0 = Sunday); a window with a profession applies only to that listing, one without applies to all.</param>
    /// <param name="professionId">The listing being booked, if any.</param>
    /// <returns>True when the booking may go ahead: no windows apply (the professional set no hours), or it fits entirely inside one window.</returns>
    public static bool Allows(IReadOnlyList<AvailabilitySlot> slots, long? professionId, DateTime startUtc, int durationMinutes, string? zoneId, string? fallbackZoneId)
    {
        var applicable = slots.Where(s => s.ProfessionId is null || s.ProfessionId == professionId).ToList();
        if (applicable.Count == 0) return true;                       // nothing set: nothing to enforce

        var zone = Find(zoneId) ?? Find(fallbackZoneId) ?? TimeZoneInfo.Utc;
        var start = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(startUtc, DateTimeKind.Utc), zone);
        var end = start.AddMinutes(durationMinutes);
        if (end.Date != start.Date) return false;                     // a window never crosses midnight, so neither may a booking inside one

        var day = (int)start.DayOfWeek;
        return applicable.Any(s => s.DayOfWeek == day && Time(s.StartTime) is { } from && Time(s.EndTime) is { } to && from <= start.TimeOfDay && end.TimeOfDay <= to);
    }

    private static TimeSpan? Time(string text) => TimeSpan.TryParse(text, CultureInfo.InvariantCulture, out var t) ? t : null;

    private static TimeZoneInfo? Find(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;
        try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException) { return null; }
    }
}
