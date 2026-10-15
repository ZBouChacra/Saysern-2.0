using System.Globalization;
using System.Text.RegularExpressions;
using Sayserne.Api.Data;

namespace Sayserne.Api.Infrastructure;

public sealed record ProfileInput(string? FirstName, string? LastName, string? Phone, string? Sex, string? DateOfBirth, string? Nationality, string? Country,
    string? City, string? Bio, string? Portfolio, string? ProfilePhoto, string? BannerPhoto, string? PreferredLanguage, string? ProfileType);
public sealed record ProfessionInput(long? CategoryId, long? ServiceId, string? Country, string? City, decimal? CostPerHour, int? YearsOfExperience, string? Website,
    bool? HasTeam, int? TeamSize, bool? HasOffice, string? OfficeAddress, string? OfficeCity, string? OfficeCountry, List<string>? GeographicAreas, bool? IsHidden);
public sealed record SlotInput(int? DayOfWeek, string? StartTime, string? EndTime);
public sealed record AvailabilityInput(long? ProfessionId, List<SlotInput>? Slots);
public sealed record BookingInput(long? ProfessionalId, long? ProfessionId, DateTimeOffset? StartsAt, int? DurationMinutes, string? Description);
public sealed record StatusInput(string? Status);
public sealed record ReviewInput(long? AppointmentId, int? Rating, string? Comment);
public sealed record StartChatInput(long? UserId);
public sealed record SendMessageInput(string? Type, string? Content, decimal? Latitude, decimal? Longitude);
public sealed record AlertInput(string? Name, SearchFilters? SearchCriteria, string? Frequency);
public sealed record FlagsInput(bool IsPremium, bool IsStarred);

/// <summary>Server-side validation. Every method either returns clean values or throws VALIDATION_FAILED with per-field messages.</summary>
public static partial class MarketplaceValidation
{
    [GeneratedRegex(@"^[0-9+()\-\s.]{5,50}$")] private static partial Regex PhonePattern();
    [GeneratedRegex(@"^([01]\d|2[0-3]):[0-5]\d$")] private static partial Regex TimePattern();

    private sealed class Errs
    {
        public Dictionary<string, string> Map { get; } = [];
        public void Add(string field, string message) => Map.TryAdd(field, message);
        public void ThrowIfAny() { if (Map.Count > 0) throw new AppException("VALIDATION_FAILED", 400, fieldErrors: Map); }
    }

    private static string? Clean(string? v, string field, int max, Errs e, bool required = false)
    {
        var t = v?.Trim();
        if (string.IsNullOrEmpty(t)) { if (required) e.Add(field, "Required."); return null; }
        if (t.Length > max) e.Add(field, $"At most {max} characters.");
        else if (t.Any(c => char.IsControl(c) && c is not ('\n' or '\r' or '\t'))) e.Add(field, "Contains invalid characters.");
        return t;
    }

    private static string? Url(string? v, string field, int max, Errs e)
    {
        var t = Clean(v, field, max, e);
        if (t != null && (!Uri.TryCreate(t, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))) e.Add(field, "Enter a valid http(s) link.");
        return t;
    }

    public static ProfileValues ValidateProfile(ProfileInput i)
    {
        var e = new Errs();
        var first = Clean(i.FirstName, "firstName", 100, e, required: true);
        var last = Clean(i.LastName, "lastName", 100, e, required: true);
        var phone = Clean(i.Phone, "phone", 50, e);
        if (phone != null && !PhonePattern().IsMatch(phone)) e.Add("phone", "Enter a valid phone number.");
        var sex = Clean(i.Sex, "sex", 10, e)?.ToLowerInvariant();
        if (sex is not (null or "male" or "female")) e.Add("sex", "Must be male or female.");
        DateOnly? dob = null;
        if (!string.IsNullOrWhiteSpace(i.DateOfBirth))
        {
            if (!DateOnly.TryParseExact(i.DateOfBirth.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)) e.Add("dateOfBirth", "Use the format yyyy-MM-dd.");
            else if (d > DateOnly.FromDateTime(DateTime.UtcNow) || d < new DateOnly(1900, 1, 1)) e.Add("dateOfBirth", "Enter a realistic date of birth.");
            else dob = d;
        }
        var language = (i.PreferredLanguage ?? "en").Trim().ToLowerInvariant();
        if (language is not ("en" or "ar")) e.Add("preferredLanguage", "Must be en or ar.");
        var type = (i.ProfileType ?? "customer").Trim().ToLowerInvariant();
        if (type is not ("customer" or "professional")) e.Add("profileType", "Must be customer or professional.");

        var values = new ProfileValues(first ?? "", last ?? "", phone, sex, dob, Clean(i.Nationality, "nationality", 100, e), Clean(i.Country, "country", 100, e),
            Clean(i.City, "city", 100, e), Clean(i.Bio, "bio", 2000, e), Clean(i.Portfolio, "portfolio", 20000, e), Url(i.ProfilePhoto, "profilePhoto", 500, e),
            Url(i.BannerPhoto, "bannerPhoto", 500, e), language, type);
        e.ThrowIfAny();
        return values;
    }

    public static (long CategoryId, long ServiceId, ProfessionValues Values, bool IsHidden) ValidateProfession(ProfessionInput i, bool requireIds)
    {
        var e = new Errs();
        if (requireIds)
        {
            if (i.CategoryId is null or <= 0) e.Add("categoryId", "Choose a category.");
            if (i.ServiceId is null or <= 0) e.Add("serviceId", "Choose a service.");
        }
        if (i.CostPerHour is < 0 or > 1_000_000) e.Add("costPerHour", "Enter an amount between 0 and 1,000,000.");
        if (i.YearsOfExperience is < 0 or > 80) e.Add("yearsOfExperience", "Enter a number between 0 and 80.");
        var hasTeam = i.HasTeam ?? false;
        var teamSize = hasTeam ? i.TeamSize ?? 0 : 0;
        if (teamSize is < 0 or > 100_000) e.Add("teamSize", "Enter a realistic team size.");
        var areas = (i.GeographicAreas ?? []).Select(a => a?.Trim() ?? "").Where(a => a.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (areas.Count > 30) e.Add("geographicAreas", "At most 30 areas.");
        if (areas.Any(a => a.Length > 100)) e.Add("geographicAreas", "Each area must be at most 100 characters.");

        var values = new ProfessionValues(Clean(i.Country, "country", 100, e), Clean(i.City, "city", 100, e), i.CostPerHour is { } c ? Math.Round(c, 2) : null,
            i.YearsOfExperience ?? 0, Url(i.Website, "website", 255, e), hasTeam, teamSize, i.HasOffice ?? false, Clean(i.OfficeAddress, "officeAddress", 500, e),
            Clean(i.OfficeCity, "officeCity", 100, e), Clean(i.OfficeCountry, "officeCountry", 100, e), areas);
        e.ThrowIfAny();
        return (i.CategoryId ?? 0, i.ServiceId ?? 0, values, i.IsHidden ?? false);
    }

    public static IReadOnlyList<SlotValues> ValidateSlots(IReadOnlyList<SlotInput>? slots)
    {
        var e = new Errs();
        slots ??= [];
        if (slots.Count > 50) e.Add("slots", "At most 50 time windows.");
        var result = new List<SlotValues>();
        for (var n = 0; n < Math.Min(slots.Count, 50); n++)
        {
            var s = slots[n];
            if (s.DayOfWeek is null or < 0 or > 6) { e.Add($"slots[{n}].dayOfWeek", "Day must be 0 (Sunday) to 6 (Saturday)."); continue; }
            if (s.StartTime is null || !TimePattern().IsMatch(s.StartTime) || s.EndTime is null || !TimePattern().IsMatch(s.EndTime)) { e.Add($"slots[{n}]", "Use HH:mm times."); continue; }
            if (string.CompareOrdinal(s.StartTime, s.EndTime) >= 0) { e.Add($"slots[{n}]", "Start must be before end."); continue; }
            result.Add(new SlotValues(s.DayOfWeek.Value, s.StartTime, s.EndTime));
        }
        foreach (var day in result.GroupBy(r => r.DayOfWeek))
        {
            var ordered = day.OrderBy(r => r.StartTime, StringComparer.Ordinal).ToList();
            for (var k = 1; k < ordered.Count; k++)
                if (string.CompareOrdinal(ordered[k].StartTime, ordered[k - 1].EndTime) < 0) e.Add("slots", "Time windows on the same day must not overlap.");
        }
        e.ThrowIfAny();
        return result;
    }

    public static SearchFilters ValidateSearch(SearchFilters f)
    {
        var e = new Errs();
        string? S(string? v, string field) => Clean(v, field, 100, e);
        var sex = S(f.Sex, "sex")?.ToLowerInvariant();
        if (sex is not (null or "male" or "female")) e.Add("sex", "Must be male or female.");
        if (f.MinAge is < 0 or > 120 || f.MaxAge is < 0 or > 120) e.Add("age", "Age must be between 0 and 120.");
        if (f.MinAge > f.MaxAge) e.Add("age", "Minimum age exceeds maximum age.");
        if (f.MinStars is < 0 or > 5) e.Add("minStars", "Between 0 and 5.");
        if (f.MinCost is < 0 or > 1_000_000 || f.MaxCost is < 0 or > 1_000_000) e.Add("cost", "Between 0 and 1,000,000.");
        if (f.MinCost > f.MaxCost) e.Add("cost", "Minimum cost exceeds maximum cost.");
        if (f.MinExperience is < 0 or > 80 || f.MaxExperience is < 0 or > 80) e.Add("experience", "Between 0 and 80.");
        if (f.MinExperience > f.MaxExperience) e.Add("experience", "Minimum experience exceeds maximum.");
        if (f.Page is < 1 or > 10_000) e.Add("page", "Invalid page.");
        if (f.PageSize is < 1 or > 50) e.Add("pageSize", "Between 1 and 50.");
        var clean = f with { FirstName = S(f.FirstName, "firstName"), LastName = S(f.LastName, "lastName"), Sex = sex, Nationality = S(f.Nationality, "nationality"),
            Country = S(f.Country, "country"), City = S(f.City, "city") };
        e.ThrowIfAny();
        return clean;
    }

    public static (DateTime StartUtc, int Duration, string? Description) ValidateBooking(BookingInput i)
    {
        var e = new Errs();
        if (i.ProfessionalId is null or <= 0) e.Add("professionalId", "Required.");
        if (i.StartsAt is null) e.Add("startsAt", "Required (ISO 8601 with offset, e.g. 2026-11-02T10:00:00Z).");
        var duration = i.DurationMinutes ?? 60;
        if (duration is < 15 or > 480) e.Add("durationMinutes", "Between 15 and 480.");
        var description = Clean(i.Description, "description", 1000, e);
        e.ThrowIfAny();
        return (i.StartsAt!.Value.UtcDateTime, duration, description);
    }

    public static (string Type, string? Content, decimal? Lat, decimal? Lng) ValidateMessage(SendMessageInput i)
    {
        var e = new Errs();
        var type = (i.Type ?? "text").Trim().ToLowerInvariant();
        string? content = null;
        if (type == "text")
        {
            content = Clean(i.Content, "content", 4000, e, required: true);
        }
        else if (type == "location")
        {
            if (i.Latitude is null or < -90 or > 90) e.Add("latitude", "Between -90 and 90.");
            if (i.Longitude is null or < -180 or > 180) e.Add("longitude", "Between -180 and 180.");
        }
        else e.Add("type", "Must be text or location.");
        e.ThrowIfAny();
        return (type, content, i.Latitude, i.Longitude);
    }

    public static (string Name, SearchFilters Criteria, string Frequency) ValidateAlert(AlertInput i)
    {
        var e = new Errs();
        var name = Clean(i.Name, "name", 100, e, required: true);
        var frequency = (i.Frequency ?? "daily").Trim().ToLowerInvariant();
        if (frequency is not ("daily" or "weekly" or "monthly")) e.Add("frequency", "Must be daily, weekly or monthly.");
        if (i.SearchCriteria is null) e.Add("searchCriteria", "Required.");
        e.ThrowIfAny();
        var criteria = ValidateSearch(i.SearchCriteria!) with { Page = null, PageSize = null };
        return (name!, criteria, frequency);
    }
}
