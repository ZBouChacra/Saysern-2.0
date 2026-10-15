using System.Text.Json;

namespace Sayserne.Api.Data;

// Repositories only translate method calls into stored-procedure calls. No SQL text here.

public sealed record ProfileValues(
    string FirstName, string LastName, string? Phone, string? Sex, DateOnly? DateOfBirth, string? Nationality, string? Country, string? City,
    string? Bio, string? Portfolio, string? ProfilePhoto, string? BannerPhoto, string PreferredLanguage, string ProfileType);

public sealed record ProfessionValues(
    string? Country, string? City, decimal? CostPerHour, int YearsOfExperience, string? Website, bool HasTeam, int TeamSize, bool HasOffice,
    string? OfficeAddress, string? OfficeCity, string? OfficeCountry, IReadOnlyList<string> GeographicAreas);

public sealed record SlotValues(int DayOfWeek, string StartTime, string EndTime);

public interface ICatalogRepository
{
    Task<IReadOnlyList<Category>> CategoriesAsync(bool onlyActive, CancellationToken ct = default);
    Task<IReadOnlyList<Service>> ServicesAsync(long? categoryId, bool onlyActive, CancellationToken ct = default);
}

public interface IProfessionalRepository
{
    Task<PrivateProfile?> GetPrivateProfileAsync(long userId, CancellationToken ct = default);
    Task<PublicProfile?> GetPublicProfileAsync(long userId, CancellationToken ct = default);
    Task<PrivateProfile> UpdateProfileAsync(long userId, ProfileValues v, CancellationToken ct = default);
    Task<IReadOnlyList<Profession>> ListProfessionsAsync(long userId, bool publicOnly, CancellationToken ct = default);
    Task<Profession> CreateProfessionAsync(long userId, long categoryId, long serviceId, ProfessionValues v, CancellationToken ct = default);
    Task<Profession> UpdateProfessionAsync(long userId, long id, ProfessionValues v, bool isHidden, CancellationToken ct = default);
    Task DeleteProfessionAsync(long userId, long id, CancellationToken ct = default);
    Task SetAvailabilityAsync(long userId, long? professionId, IReadOnlyList<SlotValues> slots, CancellationToken ct = default);
    Task<IReadOnlyList<AvailabilitySlot>> GetAvailabilityAsync(long userId, CancellationToken ct = default);
    Task<PagedResult<ProfessionalListing>> SearchAsync(SearchFilters f, CancellationToken ct = default);
    Task<IReadOnlyList<ProfessionalListing>> TopAsync(int limit, CancellationToken ct = default);
}

public interface IBookingRepository
{
    Task<Appointment> CreateAppointmentAsync(long clientId, long professionalId, long? professionId, DateTime startsAtUtc, int durationMinutes, string? description, CancellationToken ct = default);
    Task<IReadOnlyList<Appointment>> ListAppointmentsAsync(long userId, CancellationToken ct = default);
    Task<Appointment> TransitionAsync(long actorId, long appointmentId, string status, CancellationToken ct = default);
    Task<IReadOnlyList<BusySlot>> BusyAsync(long professionalId, DateTime? from, DateTime? to, CancellationToken ct = default);
    Task<ReviewCreated> CreateReviewAsync(long reviewerId, long appointmentId, int rating, string? comment, CancellationToken ct = default);
    Task<IReadOnlyList<ReviewItem>> ListReviewsAsync(long professionalId, int limit, int offset, CancellationToken ct = default);
    Task<IReadOnlyList<EligibleAppointment>> EligibleAsync(long clientId, long professionalId, CancellationToken ct = default);
}

public interface IMessagingRepository
{
    Task<ChatRoom> StartChatAsync(long userId, long otherId, CancellationToken ct = default);
    Task<IReadOnlyList<ChatRoomSummary>> RoomsAsync(long userId, CancellationToken ct = default);
    Task<IReadOnlyList<ChatMessage>> MessagesAsync(long userId, long roomId, int limit, int offset, CancellationToken ct = default);
    Task<ChatMessage> SendAsync(long userId, long roomId, string type, string? content, decimal? latitude, decimal? longitude, CancellationToken ct = default);
    Task<int> UnreadAsync(long userId, CancellationToken ct = default);
    Task<Alert> CreateAlertAsync(long userId, string name, SearchFilters criteria, string frequency, CancellationToken ct = default);
    Task<IReadOnlyList<Alert>> AlertsAsync(long userId, CancellationToken ct = default);
    Task<Alert?> GetAlertAsync(long userId, long id, CancellationToken ct = default);
    Task DeleteAlertAsync(long userId, long id, CancellationToken ct = default);
}

public sealed class CatalogRepository(IStoredProcedureExecutor db) : ICatalogRepository
{
    public Task<IReadOnlyList<Category>> CategoriesAsync(bool onlyActive, CancellationToken ct = default) =>
        db.QueryAsync("sp_catalog_categories_list", new ProcParams().Add("p_only_active", onlyActive), MarketplaceMappers.Category, ct);

    public Task<IReadOnlyList<Service>> ServicesAsync(long? categoryId, bool onlyActive, CancellationToken ct = default) =>
        db.QueryAsync("sp_catalog_services_list", new ProcParams().Add("p_category_id", categoryId).Add("p_only_active", onlyActive), MarketplaceMappers.Service, ct);
}

public sealed class ProfessionalRepository(IStoredProcedureExecutor db) : IProfessionalRepository
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public Task<PrivateProfile?> GetPrivateProfileAsync(long userId, CancellationToken ct = default) =>
        db.QuerySingleOrDefaultAsync("sp_profile_get_private", new ProcParams().Add("p_user_id", userId), MarketplaceMappers.PrivateProfile, ct);

    public Task<PublicProfile?> GetPublicProfileAsync(long userId, CancellationToken ct = default) =>
        db.QuerySingleOrDefaultAsync("sp_profile_get_public", new ProcParams().Add("p_user_id", userId), MarketplaceMappers.PublicProfile, ct);

    public async Task<PrivateProfile> UpdateProfileAsync(long userId, ProfileValues v, CancellationToken ct = default) =>
        (await db.QuerySingleOrDefaultAsync("sp_profile_update",
            new ProcParams().Add("p_user_id", userId).Add("p_first_name", v.FirstName).Add("p_last_name", v.LastName).Add("p_phone", v.Phone)
                .Add("p_sex", v.Sex).Add("p_date_of_birth", v.DateOfBirth?.ToDateTime(TimeOnly.MinValue)).Add("p_nationality", v.Nationality)
                .Add("p_country", v.Country).Add("p_city", v.City).Add("p_bio", v.Bio).Add("p_portfolio", v.Portfolio)
                .Add("p_profile_photo", v.ProfilePhoto).Add("p_banner_photo", v.BannerPhoto)
                .Add("p_preferred_language", v.PreferredLanguage).Add("p_profile_type", v.ProfileType),
            MarketplaceMappers.PrivateProfile, ct))!;

    public Task<IReadOnlyList<Profession>> ListProfessionsAsync(long userId, bool publicOnly, CancellationToken ct = default) =>
        db.QueryAsync(publicOnly ? "sp_professions_list_public" : "sp_professions_list_by_user", new ProcParams().Add("p_user_id", userId), MarketplaceMappers.Profession, ct);

    public async Task<Profession> CreateProfessionAsync(long userId, long categoryId, long serviceId, ProfessionValues v, CancellationToken ct = default) =>
        (await db.QuerySingleOrDefaultAsync("sp_professions_create",
            new ProcParams().Add("p_user_id", userId).Add("p_category_id", categoryId).Add("p_service_id", serviceId).Add("p_country", v.Country)
                .Add("p_city", v.City).Add("p_cost_per_hour", v.CostPerHour).Add("p_years", v.YearsOfExperience).Add("p_website", v.Website)
                .Add("p_has_team", v.HasTeam).Add("p_team_size", v.TeamSize).Add("p_has_office", v.HasOffice).Add("p_office_address", v.OfficeAddress)
                .Add("p_office_city", v.OfficeCity).Add("p_office_country", v.OfficeCountry).Add("p_geographic_areas", Areas(v)),
            MarketplaceMappers.Profession, ct))!;

    public async Task<Profession> UpdateProfessionAsync(long userId, long id, ProfessionValues v, bool isHidden, CancellationToken ct = default) =>
        (await db.QuerySingleOrDefaultAsync("sp_professions_update",
            new ProcParams().Add("p_user_id", userId).Add("p_id", id).Add("p_country", v.Country).Add("p_city", v.City).Add("p_cost_per_hour", v.CostPerHour)
                .Add("p_years", v.YearsOfExperience).Add("p_website", v.Website).Add("p_has_team", v.HasTeam).Add("p_team_size", v.TeamSize)
                .Add("p_has_office", v.HasOffice).Add("p_office_address", v.OfficeAddress).Add("p_office_city", v.OfficeCity)
                .Add("p_office_country", v.OfficeCountry).Add("p_geographic_areas", Areas(v)).Add("p_is_hidden", isHidden),
            MarketplaceMappers.Profession, ct))!;

    public Task DeleteProfessionAsync(long userId, long id, CancellationToken ct = default) =>
        db.ExecuteAsync("sp_professions_delete", new ProcParams().Add("p_user_id", userId).Add("p_id", id), ct);

    public Task SetAvailabilityAsync(long userId, long? professionId, IReadOnlyList<SlotValues> slots, CancellationToken ct = default) =>
        db.ExecuteAsync("sp_availability_set",
            new ProcParams().Add("p_user_id", userId).Add("p_profession_id", professionId).Add("p_slots", JsonSerializer.Serialize(slots, Json)), ct);

    public Task<IReadOnlyList<AvailabilitySlot>> GetAvailabilityAsync(long userId, CancellationToken ct = default) =>
        db.QueryAsync("sp_availability_list_by_user", new ProcParams().Add("p_user_id", userId), MarketplaceMappers.Slot, ct);

    public async Task<PagedResult<ProfessionalListing>> SearchAsync(SearchFilters f, CancellationToken ct = default)
    {
        var page = Math.Max(f.Page ?? 1, 1);
        var size = Math.Clamp(f.PageSize ?? 20, 1, 50);
        var (rows, totals) = await db.QueryTwoAsync("sp_professionals_search",
            new ProcParams().Add("p_category_id", f.CategoryId).Add("p_service_id", f.ServiceId).Add("p_first_name", f.FirstName).Add("p_last_name", f.LastName)
                .Add("p_sex", f.Sex).Add("p_min_age", f.MinAge).Add("p_max_age", f.MaxAge).Add("p_nationality", f.Nationality).Add("p_country", f.Country)
                .Add("p_city", f.City).Add("p_has_office", f.HasOffice).Add("p_min_stars", f.MinStars).Add("p_min_cost", f.MinCost).Add("p_max_cost", f.MaxCost)
                .Add("p_min_experience", f.MinExperience).Add("p_max_experience", f.MaxExperience).Add("p_has_team", f.HasTeam)
                .Add("p_page", page).Add("p_page_size", size),
            MarketplaceMappers.Listing, r => r.Long("total"), ct);
        return new PagedResult<ProfessionalListing>(rows, totals.Count > 0 ? totals[0] : 0, page, size);
    }

    public Task<IReadOnlyList<ProfessionalListing>> TopAsync(int limit, CancellationToken ct = default) =>
        db.QueryAsync("sp_professionals_top", new ProcParams().Add("p_limit", Math.Clamp(limit, 1, 50)), MarketplaceMappers.Listing, ct);

    private static string Areas(ProfessionValues v) => JsonSerializer.Serialize(v.GeographicAreas, Json);
}

public sealed class BookingRepository(IStoredProcedureExecutor db) : IBookingRepository
{
    public async Task<Appointment> CreateAppointmentAsync(long clientId, long professionalId, long? professionId, DateTime startsAtUtc, int durationMinutes, string? description, CancellationToken ct = default) =>
        (await db.QuerySingleOrDefaultAsync("sp_appointments_create",
            new ProcParams().Add("p_client_id", clientId).Add("p_professional_id", professionalId).Add("p_profession_id", professionId)
                .Add("p_starts_at", startsAtUtc).Add("p_duration_minutes", durationMinutes).Add("p_description", description),
            MarketplaceMappers.Appointment, ct))!;

    public Task<IReadOnlyList<Appointment>> ListAppointmentsAsync(long userId, CancellationToken ct = default) =>
        db.QueryAsync("sp_appointments_list_by_user", new ProcParams().Add("p_user_id", userId), MarketplaceMappers.Appointment, ct);

    public async Task<Appointment> TransitionAsync(long actorId, long appointmentId, string status, CancellationToken ct = default) =>
        (await db.QuerySingleOrDefaultAsync("sp_appointments_transition",
            new ProcParams().Add("p_actor_id", actorId).Add("p_id", appointmentId).Add("p_status", status), MarketplaceMappers.Appointment, ct))!;

    public Task<IReadOnlyList<BusySlot>> BusyAsync(long professionalId, DateTime? from, DateTime? to, CancellationToken ct = default) =>
        db.QueryAsync("sp_appointments_busy", new ProcParams().Add("p_professional_id", professionalId).Add("p_from", from).Add("p_to", to), MarketplaceMappers.Busy, ct);

    public async Task<ReviewCreated> CreateReviewAsync(long reviewerId, long appointmentId, int rating, string? comment, CancellationToken ct = default) =>
        (await db.QuerySingleOrDefaultAsync("sp_reviews_create",
            new ProcParams().Add("p_reviewer_id", reviewerId).Add("p_appointment_id", appointmentId).Add("p_rating", rating).Add("p_comment", comment),
            MarketplaceMappers.ReviewCreated, ct))!;

    public Task<IReadOnlyList<ReviewItem>> ListReviewsAsync(long professionalId, int limit, int offset, CancellationToken ct = default) =>
        db.QueryAsync("sp_reviews_list_by_professional",
            new ProcParams().Add("p_professional_id", professionalId).Add("p_limit", limit).Add("p_offset", offset), MarketplaceMappers.ReviewItem, ct);

    public Task<IReadOnlyList<EligibleAppointment>> EligibleAsync(long clientId, long professionalId, CancellationToken ct = default) =>
        db.QueryAsync("sp_reviews_eligible", new ProcParams().Add("p_client_id", clientId).Add("p_professional_id", professionalId), MarketplaceMappers.Eligible, ct);
}

public sealed class MessagingRepository(IStoredProcedureExecutor db) : IMessagingRepository
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull };

    public async Task<ChatRoom> StartChatAsync(long userId, long otherId, CancellationToken ct = default) =>
        (await db.QuerySingleOrDefaultAsync("sp_chat_start", new ProcParams().Add("p_user_id", userId).Add("p_other_id", otherId), MarketplaceMappers.Room, ct))!;

    public Task<IReadOnlyList<ChatRoomSummary>> RoomsAsync(long userId, CancellationToken ct = default) =>
        db.QueryAsync("sp_chat_rooms_list", new ProcParams().Add("p_user_id", userId), MarketplaceMappers.RoomSummary, ct);

    public Task<IReadOnlyList<ChatMessage>> MessagesAsync(long userId, long roomId, int limit, int offset, CancellationToken ct = default) =>
        db.QueryAsync("sp_chat_messages_list",
            new ProcParams().Add("p_user_id", userId).Add("p_room_id", roomId).Add("p_limit", limit).Add("p_offset", offset), MarketplaceMappers.Message, ct);

    public async Task<ChatMessage> SendAsync(long userId, long roomId, string type, string? content, decimal? latitude, decimal? longitude, CancellationToken ct = default) =>
        (await db.QuerySingleOrDefaultAsync("sp_chat_send",
            new ProcParams().Add("p_user_id", userId).Add("p_room_id", roomId).Add("p_type", type).Add("p_content", content)
                .Add("p_latitude", latitude).Add("p_longitude", longitude), MarketplaceMappers.Message, ct))!;

    public async Task<int> UnreadAsync(long userId, CancellationToken ct = default)
    {
        var rows = await db.QueryAsync("sp_chat_unread_count", new ProcParams().Add("p_user_id", userId), r => r.Int("unread"), ct);
        return rows.Count > 0 ? rows[0] : 0;
    }

    public async Task<Alert> CreateAlertAsync(long userId, string name, SearchFilters criteria, string frequency, CancellationToken ct = default) =>
        (await db.QuerySingleOrDefaultAsync("sp_alerts_create",
            new ProcParams().Add("p_user_id", userId).Add("p_name", name).Add("p_criteria", JsonSerializer.Serialize(criteria, Json)).Add("p_frequency", frequency),
            MarketplaceMappers.Alert, ct))!;

    public Task<IReadOnlyList<Alert>> AlertsAsync(long userId, CancellationToken ct = default) =>
        db.QueryAsync("sp_alerts_list", new ProcParams().Add("p_user_id", userId), MarketplaceMappers.Alert, ct);

    public Task<Alert?> GetAlertAsync(long userId, long id, CancellationToken ct = default) =>
        db.QuerySingleOrDefaultAsync("sp_alerts_get", new ProcParams().Add("p_user_id", userId).Add("p_id", id), MarketplaceMappers.Alert, ct);

    public Task DeleteAlertAsync(long userId, long id, CancellationToken ct = default) =>
        db.ExecuteAsync("sp_alerts_delete", new ProcParams().Add("p_user_id", userId).Add("p_id", id), ct);
}
