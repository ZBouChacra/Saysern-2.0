using System.Globalization;

namespace Sayserne.Api.Email;

/// <summary>Turns a UTC moment into the text an e-mail shows: in the recipient's own time zone (or the site's default), always with its offset, so nobody has to guess.</summary>
public static class EmailTimes
{
    public const string UtcSuffix = "_utc";

    public static string Format(DateTime utc, string? zoneId, string? fallbackZoneId)
    {
        var zone = Find(zoneId) ?? Find(fallbackZoneId) ?? TimeZoneInfo.Utc;
        var moment = DateTime.SpecifyKind(utc, DateTimeKind.Utc);
        var local = TimeZoneInfo.ConvertTimeFromUtc(moment, zone);
        var offset = zone.GetUtcOffset(moment);
        var sign = offset < TimeSpan.Zero ? "-" : "+";
        return $"{local.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)} (UTC{sign}{Math.Abs(offset.Hours):00}:{Math.Abs(offset.Minutes):00})";
    }

    private static TimeZoneInfo? Find(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;
        try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException) { return null; }   // an unknown name must never stop an e-mail
    }

    /// <summary>For every "x_utc" value (an ISO moment) adds the "x" text for this recipient.</summary>
    public static Dictionary<string, string> Localise(IReadOnlyDictionary<string, string> data, string? zoneId, string? fallbackZoneId)
    {
        var values = new Dictionary<string, string>(data);
        foreach (var (key, value) in data)
            if (key.EndsWith(UtcSuffix, StringComparison.Ordinal) && DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var utc))
                values[key[..^UtcSuffix.Length]] = Format(utc, zoneId, fallbackZoneId);
        return values;
    }
}
