using Microsoft.AspNetCore.Mvc;
using Sayserne.Api.Auth;
using Sayserne.Api.Data;
using Sayserne.Api.Infrastructure;
using Sayserne.Api.Platform;

namespace Sayserne.Api.Endpoints;

public static class MarketplaceEndpoints
{
    public const string WriteLimiter = "write";

    public static void MapMarketplace(this IEndpointRouteBuilder app)
    {
        MapCatalog(app);
        MapProfessionals(app);
        MapMyProfile(app);
        MapBookings(app);
        MapChat(app);
        MapAlerts(app);
    }

    // ---------------------------------------------------------------- public catalog
    private static void MapCatalog(IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/catalog");
        g.MapGet("/categories", (ICatalogRepository catalog) => catalog.CategoriesAsync(onlyActive: true));
        g.MapGet("/services", (ICatalogRepository catalog, long? categoryId) => catalog.ServicesAsync(categoryId, onlyActive: true));
    }

    // ---------------------------------------------------------------- public professionals
    private static void MapProfessionals(IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/professionals");

        g.MapGet("/top", (IProfessionalRepository repo, int? limit) => repo.TopAsync(limit ?? 5));

        g.MapGet("/search", async (IProfessionalRepository repo, ISettingsService settings, [AsParameters] SearchFilters filters) =>
            Results.Ok(await repo.SearchAsync(MarketplaceValidation.ValidateSearch(filters, settings))));

        g.MapGet("/{userId:long}", async (long userId, IProfessionalRepository repo) =>
        {
            var profile = await repo.GetPublicProfileAsync(userId) ?? throw new AppException("NOT_FOUND");
            var professions = await repo.ListProfessionsAsync(userId, publicOnly: true);
            var visible = professions.Select(p => p.Id).ToHashSet();
            var availability = (await repo.GetAvailabilityAsync(userId)).Where(a => a.ProfessionId is null || visible.Contains(a.ProfessionId.Value)).ToList();
            return Results.Ok(new { profile, professions, availability });
        });

        // When the professional is busy: never who booked or why.
        g.MapGet("/{userId:long}/busy", async (long userId, IBookingRepository bookings, IProfessionalRepository repo, DateTimeOffset? from, DateTimeOffset? to) =>
        {
            if (await repo.GetPublicProfileAsync(userId) is null) throw new AppException("NOT_FOUND");
            return Results.Ok(await bookings.BusyAsync(userId, from?.UtcDateTime, to?.UtcDateTime));
        });

        g.MapGet("/{userId:long}/reviews", async (long userId, IBookingRepository bookings, int? limit, int? offset) =>
            Results.Ok(await bookings.ListReviewsAsync(userId, limit ?? 50, offset ?? 0)));
    }

    // ---------------------------------------------------------------- the signed-in user's own profile & listings
    private static void MapMyProfile(IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/me").RequireAuthorization().RequireRateLimiting(WriteLimiter);

        g.MapGet("/profile", async (HttpContext ctx, IProfessionalRepository repo) =>
            Results.Ok(await repo.GetPrivateProfileAsync(ctx.UserId()) ?? throw new AppException("USER_NOT_FOUND")));

        g.MapPut("/profile", async (HttpContext ctx, ProfileInput input, IProfessionalRepository repo, ISettingsService settings) =>
            Results.Ok(await repo.UpdateProfileAsync(ctx.UserId(), MarketplaceValidation.ValidateProfile(input, settings))));

        g.MapGet("/professions", async (HttpContext ctx, IProfessionalRepository repo) => Results.Ok(await repo.ListProfessionsAsync(ctx.UserId(), publicOnly: false)));

        g.MapPost("/professions", async (HttpContext ctx, ProfessionInput input, IProfessionalRepository repo, ISettingsService settings) =>
        {
            var (categoryId, serviceId, values, _) = MarketplaceValidation.ValidateProfession(input, requireIds: true, settings);
            var created = await repo.CreateProfessionAsync(ctx.UserId(), categoryId, serviceId, values);
            return Results.Json(created, statusCode: StatusCodes.Status201Created);
        });

        g.MapPut("/professions/{id:long}", async (HttpContext ctx, long id, ProfessionInput input, IProfessionalRepository repo, ISettingsService settings) =>
        {
            var (_, _, values, hidden) = MarketplaceValidation.ValidateProfession(input, requireIds: false, settings);
            return Results.Ok(await repo.UpdateProfessionAsync(ctx.UserId(), id, values, hidden));
        });

        g.MapDelete("/professions/{id:long}", async (HttpContext ctx, long id, IProfessionalRepository repo) =>
        {
            await repo.DeleteProfessionAsync(ctx.UserId(), id);
            return Results.NoContent();
        });

        g.MapGet("/availability", async (HttpContext ctx, IProfessionalRepository repo) => Results.Ok(await repo.GetAvailabilityAsync(ctx.UserId())));

        g.MapPut("/availability", async (HttpContext ctx, AvailabilityInput input, IProfessionalRepository repo) =>
        {
            await repo.SetAvailabilityAsync(ctx.UserId(), input.ProfessionId, MarketplaceValidation.ValidateSlots(input.Slots));
            return Results.Ok(await repo.GetAvailabilityAsync(ctx.UserId()));
        });
    }

    // ---------------------------------------------------------------- appointments & reviews
    private static void MapBookings(IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/appointments").RequireAuthorization().RequireRateLimiting(WriteLimiter);

        g.MapPost("", async (HttpContext ctx, BookingInput input, IBookingRepository bookings, ISettingsService settings) =>
        {
            var (start, duration, description) = MarketplaceValidation.ValidateBooking(input, settings);
            var created = await bookings.CreateAppointmentAsync(ctx.UserId(), input.ProfessionalId!.Value, input.ProfessionId, start, duration, description);
            return Results.Json(created, statusCode: StatusCodes.Status201Created);
        });

        g.MapGet("", async (HttpContext ctx, IBookingRepository bookings) => Results.Ok(await bookings.ListAppointmentsAsync(ctx.UserId())));

        g.MapPost("/{id:long}/status", async (HttpContext ctx, long id, StatusInput input, IBookingRepository bookings) =>
            Results.Ok(await bookings.TransitionAsync(ctx.UserId(), id, (input.Status ?? "").Trim().ToLowerInvariant())));

        var r = app.MapGroup("/api/reviews").RequireAuthorization().RequireRateLimiting(WriteLimiter);

        r.MapGet("/eligible", async (HttpContext ctx, IBookingRepository bookings, long professionalId) =>
            Results.Ok(await bookings.EligibleAsync(ctx.UserId(), professionalId)));

        r.MapPost("", async (HttpContext ctx, ReviewInput input, IBookingRepository bookings, ISettingsService settings) =>
        {
            var errors = new Dictionary<string, string>();
            if (input.AppointmentId is null or <= 0) errors["appointmentId"] = "Required.";
            if (input.Rating is null or < 1 or > 5) errors["rating"] = "Between 1 and 5.";
            var comment = input.Comment?.Trim();
            var maxComment = settings.GetInt("Reviews.MaxCommentLength");
            if (comment?.Length > maxComment) errors["comment"] = $"At most {maxComment} characters.";
            if (errors.Count > 0) throw new AppException("VALIDATION_FAILED", 400, fieldErrors: errors);
            var created = await bookings.CreateReviewAsync(ctx.UserId(), input.AppointmentId!.Value, input.Rating!.Value, string.IsNullOrEmpty(comment) ? null : comment);
            return Results.Json(created, statusCode: StatusCodes.Status201Created);
        });
    }

    // ---------------------------------------------------------------- chat
    private static void MapChat(IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/chat").RequireAuthorization().RequireRateLimiting(WriteLimiter);

        g.MapGet("/rooms", async (HttpContext ctx, IMessagingRepository chat) => Results.Ok(await chat.RoomsAsync(ctx.UserId())));

        g.MapPost("/rooms", async (HttpContext ctx, StartChatInput input, IMessagingRepository chat) =>
        {
            if (input.UserId is null or <= 0) throw new AppException("VALIDATION_FAILED", 400, fieldErrors: new Dictionary<string, string> { ["userId"] = "Required." });
            return Results.Ok(await chat.StartChatAsync(ctx.UserId(), input.UserId.Value));
        });

        g.MapGet("/rooms/{roomId:long}/messages", async (HttpContext ctx, long roomId, IMessagingRepository chat, int? limit, int? offset) =>
            Results.Ok(await chat.MessagesAsync(ctx.UserId(), roomId, limit ?? 50, offset ?? 0)));

        g.MapPost("/rooms/{roomId:long}/messages", async (HttpContext ctx, long roomId, SendMessageInput input, IMessagingRepository chat, ISettingsService settings) =>
        {
            var (type, content, lat, lng, fileId) = MarketplaceValidation.ValidateMessage(input, settings);
            return Results.Json(await chat.SendAsync(ctx.UserId(), roomId, type, content, lat, lng, fileId), statusCode: StatusCodes.Status201Created);
        });

        g.MapGet("/unread-count", async (HttpContext ctx, IMessagingRepository chat) => Results.Ok(new { unread = await chat.UnreadAsync(ctx.UserId()) }));
    }

    // ---------------------------------------------------------------- saved searches
    private static void MapAlerts(IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/alerts").RequireAuthorization().RequireRateLimiting(WriteLimiter);

        g.MapGet("", async (HttpContext ctx, IMessagingRepository repo) => Results.Ok(await repo.AlertsAsync(ctx.UserId())));

        g.MapPost("", async (HttpContext ctx, AlertInput input, IMessagingRepository repo, ISettingsService settings) =>
        {
            var (name, criteria, frequency) = MarketplaceValidation.ValidateAlert(input, settings);
            return Results.Json(await repo.CreateAlertAsync(ctx.UserId(), name, criteria, frequency), statusCode: StatusCodes.Status201Created);
        });

        g.MapDelete("/{id:long}", async (HttpContext ctx, long id, IMessagingRepository repo) =>
        {
            await repo.DeleteAlertAsync(ctx.UserId(), id);
            return Results.NoContent();
        });

        g.MapGet("/{id:long}/run", async (HttpContext ctx, long id, IMessagingRepository repo, IProfessionalRepository professionals, int? page) =>
        {
            var alert = await repo.GetAlertAsync(ctx.UserId(), id) ?? throw new AppException("ALERT_NOT_FOUND");
            return Results.Ok(await professionals.SearchAsync(alert.SearchCriteria with { Page = page }));
        });
    }
}
