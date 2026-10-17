using System.Data.Common;
using System.Text.Json;

namespace Sayserne.Api.Data;

// ---- catalog ----
public sealed record Category(long Id, string Name, string? NameAr, string? Description, string? DescriptionAr, string? Icon, bool IsBlocked);
public sealed record Service(long Id, long CategoryId, string Name, string? NameAr, string? Description, string? DescriptionAr, bool IsBlocked);

// ---- profiles ----
/// <summary>What the owner sees about themselves.</summary>
public sealed record PrivateProfile(
    long Id, string Email, bool EmailVerified, string FirstName, string LastName, string? Phone, string? Sex, DateOnly? DateOfBirth,
    string? Nationality, string? Country, string? City, string? ProfilePhoto, string? BannerPhoto, string? Bio, string? Portfolio,
    string PreferredLanguage, string ProfileType, string Role, bool IsPremium, bool IsStarred);

/// <summary>What anyone sees: no email, phone or date of birth.</summary>
public sealed record PublicProfile(
    long Id, string FirstName, string LastName, string? Sex, string? Nationality, string? Country, string? City,
    string? ProfilePhoto, string? BannerPhoto, string? Bio, string? Portfolio, int? Age, bool IsPremium, bool IsStarred, string? TimeZone = null);

public sealed record Profession(
    long Id, long UserId, long CategoryId, long ServiceId, string CategoryName, string? CategoryNameAr, string ServiceName, string? ServiceNameAr,
    string? Country, string? City, decimal? CostPerHour, int YearsOfExperience, string? Website, bool HasTeam, int TeamSize, bool HasOffice,
    string? OfficeAddress, string? OfficeCity, string? OfficeCountry, IReadOnlyList<string> GeographicAreas, decimal AvgRating, int TotalReviews,
    bool IsHidden, bool IsLocked);

public sealed record AvailabilitySlot(long? ProfessionId, int DayOfWeek, string StartTime, string EndTime);

public sealed record ProfessionalListing(
    long UserId, long ProfessionId, long CategoryId, long ServiceId, string CategoryName, string? CategoryNameAr, string ServiceName, string? ServiceNameAr,
    decimal? CostPerHour, int YearsOfExperience, decimal AvgRating, int TotalReviews, bool HasTeam, int TeamSize, IReadOnlyList<string> GeographicAreas,
    string FirstName, string LastName, string? ProfilePhoto, string? Sex, string? Nationality, int? Age, bool IsPremium, bool IsStarred,
    string? Country, string? City, bool HasOffice, string? OfficeCity, string? OfficeCountry);

/// <summary>Search filters. Also the shape stored in a saved alert.</summary>
public sealed record SearchFilters(
    long? CategoryId = null, long? ServiceId = null, string? FirstName = null, string? LastName = null, string? Sex = null,
    int? MinAge = null, int? MaxAge = null, string? Nationality = null, string? Country = null, string? City = null,
    bool? HasOffice = null, decimal? MinStars = null, decimal? MinCost = null, decimal? MaxCost = null,
    int? MinExperience = null, int? MaxExperience = null, bool? HasTeam = null, int? Page = null, int? PageSize = null,
    string? VisitorCountry = null, string? VisitorCity = null);   // where the visitor is: it only affects the ORDER of results (own city, then own country, then elsewhere), never what is found

// ---- bookings ----
public sealed record BusySlot(DateTime StartsAt, DateTime EndsAt);

public sealed record Appointment(
    long Id, long ClientId, long ProfessionalId, long? ProfessionId, long? ServiceId, DateTime StartsAt, DateTime EndsAt, int DurationMinutes,
    string? Description, string Status, DateTime CreatedAt, string ClientFirstName, string ClientLastName, string? ClientPhoto,
    string ProfessionalFirstName, string ProfessionalLastName, string? ProfessionalPhoto, string? ServiceName, string? CategoryName, bool HasReview);

public sealed record ReviewCreated(long Id, long AppointmentId, long ProfessionalId, int Rating, string? Comment, DateTime CreatedAt);
public sealed record ReviewItem(long Id, int Rating, string? Comment, DateTime CreatedAt, string ReviewerFirstName, string ReviewerLastName, string? ReviewerPhoto, string? ReplyText = null, DateTime? RepliedAt = null, DateTime? ReplyEditedAt = null);
public sealed record ReviewReply(ReviewItem Review, long ReviewerId, bool FirstReply);
public sealed record EligibleAppointment(long Id, DateTime StartsAt);

// ---- messaging ----
public sealed record ChatRoom(long Id, DateTime LastMessageAt, DateTime CreatedAt);
public sealed record ChatRoomSummary(
    long Id, long OtherUserId, string OtherFirstName, string OtherLastName, string? OtherPhoto, DateTime LastMessageAt,
    string? LastMessageType, string? LastMessageContent, long? LastMessageSenderId, int UnreadCount);
public sealed record ChatMessage(long Id, long RoomId, long SenderId, string MessageType, string? Content, decimal? Latitude, decimal? Longitude, string? FileId, bool IsRead, DateTime CreatedAt, bool IsStaff = false, bool IsDeleted = false);
public sealed record Alert(long Id, string Name, SearchFilters SearchCriteria, string Frequency, DateTime CreatedAt);

internal static class MarketplaceMappers
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static IReadOnlyList<string> Areas(string? json) =>
        string.IsNullOrWhiteSpace(json) ? [] : JsonSerializer.Deserialize<List<string>>(json, Json) ?? [];

    public static Category Category(DbDataReader r) => new(r.Long("id"), r.Str("name"), r.StrOrNull("name_ar"), r.StrOrNull("description"), r.StrOrNull("description_ar"), r.StrOrNull("icon"), r.Bool("is_blocked"));
    public static Service Service(DbDataReader r) => new(r.Long("id"), r.Long("category_id"), r.Str("name"), r.StrOrNull("name_ar"), r.StrOrNull("description"), r.StrOrNull("description_ar"), r.Bool("is_blocked"));

    public static PrivateProfile PrivateProfile(DbDataReader r) => new(
        r.Long("id"), r.Str("email"), r.Bool("email_verified"), r.Str("first_name"), r.Str("last_name"), r.StrOrNull("phone"), r.StrOrNull("sex"),
        r.DateOrNull("date_of_birth"), r.StrOrNull("nationality"), r.StrOrNull("country"), r.StrOrNull("city"), r.StrOrNull("profile_photo"),
        r.StrOrNull("banner_photo"), r.StrOrNull("bio"), r.StrOrNull("portfolio"), r.Str("preferred_language"), r.Str("profile_type"), r.Str("role"),
        r.Bool("is_premium"), r.Bool("is_starred"));

    public static PublicProfile PublicProfile(DbDataReader r) => new(
        r.Long("id"), r.Str("first_name"), r.Str("last_name"), r.StrOrNull("sex"), r.StrOrNull("nationality"), r.StrOrNull("country"), r.StrOrNull("city"),
        r.StrOrNull("profile_photo"), r.StrOrNull("banner_photo"), r.StrOrNull("bio"), r.StrOrNull("portfolio"), r.IntOrNull("age"),
        r.Bool("is_premium"), r.Bool("is_starred"), r.StrOrNull("time_zone"));

    public static Profession Profession(DbDataReader r) => new(
        r.Long("id"), r.Long("user_id"), r.Long("category_id"), r.Long("service_id"), r.Str("category_name"), r.StrOrNull("category_name_ar"),
        r.Str("service_name"), r.StrOrNull("service_name_ar"), r.StrOrNull("country"), r.StrOrNull("city"), r.DecOrNull("cost_per_hour"),
        r.Int("years_of_experience"), r.StrOrNull("website"), r.Bool("has_team"), r.Int("team_size"), r.Bool("has_office"),
        r.StrOrNull("office_address"), r.StrOrNull("office_city"), r.StrOrNull("office_country"), Areas(r.StrOrNull("geographic_areas")),
        r.Dec("avg_rating"), r.Int("total_reviews"), r.Bool("is_hidden"), r.Bool("is_locked"));

    public static AvailabilitySlot Slot(DbDataReader r) => new(r.LongOrNull("profession_id"), r.Int("day_of_week"), r.Str("start_time"), r.Str("end_time"));

    public static ProfessionalListing Listing(DbDataReader r) => new(
        r.Long("user_id"), r.Long("profession_id"), r.Long("category_id"), r.Long("service_id"), r.Str("category_name"), r.StrOrNull("category_name_ar"),
        r.Str("service_name"), r.StrOrNull("service_name_ar"), r.DecOrNull("cost_per_hour"), r.Int("years_of_experience"), r.Dec("avg_rating"),
        r.Int("total_reviews"), r.Bool("has_team"), r.Int("team_size"), Areas(r.StrOrNull("geographic_areas")), r.Str("first_name"), r.Str("last_name"),
        r.StrOrNull("profile_photo"), r.StrOrNull("sex"), r.StrOrNull("nationality"), r.IntOrNull("age"), r.Bool("is_premium"), r.Bool("is_starred"),
        r.StrOrNull("country"), r.StrOrNull("city"), r.Bool("has_office"), r.StrOrNull("office_city"), r.StrOrNull("office_country"));

    public static BusySlot Busy(DbDataReader r) => new(r.Utc("starts_at"), r.Utc("ends_at"));

    public static Appointment Appointment(DbDataReader r) => new(
        r.Long("id"), r.Long("client_id"), r.Long("professional_id"), r.LongOrNull("profession_id"), r.LongOrNull("service_id"),
        r.Utc("starts_at"), r.Utc("ends_at"), r.Int("duration_minutes"), r.StrOrNull("description"), r.Str("status"), r.Utc("created_at"),
        r.Str("client_first_name"), r.Str("client_last_name"), r.StrOrNull("client_photo"),
        r.Str("professional_first_name"), r.Str("professional_last_name"), r.StrOrNull("professional_photo"),
        r.StrOrNull("service_name"), r.StrOrNull("category_name"), r.Bool("has_review"));

    public static ReviewCreated ReviewCreated(DbDataReader r) => new(r.Long("id"), r.Long("appointment_id"), r.Long("professional_id"), r.Int("rating"), r.StrOrNull("comment"), r.Utc("created_at"));
    public static ReviewItem ReviewItem(DbDataReader r) => new(r.Long("id"), r.Int("rating"), r.StrOrNull("comment"), r.Utc("created_at"), r.Str("reviewer_first_name"), r.Str("reviewer_last_name"), r.StrOrNull("reviewer_photo"), r.StrOrNull("reply_text"), r.UtcOrNull("replied_at"), r.UtcOrNull("reply_edited_at"));
    public static EligibleAppointment Eligible(DbDataReader r) => new(r.Long("id"), r.Utc("starts_at"));

    public static ChatRoom Room(DbDataReader r) => new(r.Long("id"), r.Utc("last_message_at"), r.Utc("created_at"));
    public static ChatRoomSummary RoomSummary(DbDataReader r) => new(
        r.Long("id"), r.Long("other_user_id"), r.Str("other_first_name"), r.Str("other_last_name"), r.StrOrNull("other_photo"), r.Utc("last_message_at"),
        r.StrOrNull("last_message_type"), r.StrOrNull("last_message_content"), r.LongOrNull("last_message_sender_id"), r.Int("unread_count"));
    public static ChatMessage Message(DbDataReader r) => new(
        r.Long("id"), r.Long("room_id"), r.Long("sender_id"), r.Str("message_type"), r.StrOrNull("content"), r.DecOrNull("latitude"), r.DecOrNull("longitude"), r.StrOrNull("file_id"),
        r.Bool("is_read"), r.Utc("created_at"), r.Bool("is_staff"), r.Bool("is_deleted"));

    public static Alert Alert(DbDataReader r) => new(
        r.Long("id"), r.Str("name"), JsonSerializer.Deserialize<SearchFilters>(r.Str("search_criteria"), Json) ?? new SearchFilters(), r.Str("frequency"), r.Utc("created_at"));
}
