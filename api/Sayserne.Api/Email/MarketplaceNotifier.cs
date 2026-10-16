using System.Globalization;
using Sayserne.Api.Data;
using Sayserne.Api.Jobs;
using Sayserne.Api.Platform;

namespace Sayserne.Api.Email;

/// <summary>Turns marketplace events (bookings, reviews, messages) into notifications. Every method swallows its own errors: an e-mail problem never fails the booking or the message.</summary>
public sealed class MarketplaceNotifier(INotifier notifier, AccountEmails links, IJobQueue jobs, IMessagingRepository chat, ISettingsService settings, TimeProvider clock, ILogger<MarketplaceNotifier> log)
{
    private static string When(DateTime utc) => utc.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) + " UTC";
    private static string Name(string first, string last) => $"{first} {last}".Trim();

    private async Task Safely(Func<Task> work)
    {
        try { await work(); }
        catch (Exception ex) when (ex is not OperationCanceledException) { log.LogWarning(ex, "A notification could not be queued."); }
    }

    public Task BookingRequested(Appointment a) => Safely(() => notifier.NotifyAsync("booking_requested", a.ProfessionalId, new Dictionary<string, string>
    {
        ["client"] = Name(a.ClientFirstName, a.ClientLastName), ["when"] = When(a.StartsAt), ["note"] = string.IsNullOrWhiteSpace(a.Description) ? "—" : a.Description!, ["link"] = links.Link("/appointments"),
    }));

    /// <summary>After a status change: approved/completed tell the client, cancelled tells whoever did NOT cancel.</summary>
    public Task BookingChanged(Appointment a, long actorId) => Safely(async () =>
    {
        var professional = Name(a.ProfessionalFirstName, a.ProfessionalLastName);
        switch (a.Status)
        {
            case "approved":
                await notifier.NotifyAsync("booking_approved", a.ClientId, new Dictionary<string, string> { ["professional"] = professional, ["when"] = When(a.StartsAt), ["link"] = links.Link("/appointments") });
                break;
            case "cancelled":
                var byClient = actorId == a.ClientId;
                await notifier.NotifyAsync("booking_cancelled", byClient ? a.ProfessionalId : a.ClientId,
                    new Dictionary<string, string> { ["other"] = byClient ? Name(a.ClientFirstName, a.ClientLastName) : professional, ["when"] = When(a.StartsAt), ["link"] = links.Link("/appointments") });
                break;
            case "completed":
                await notifier.NotifyAsync("booking_completed", a.ClientId, new Dictionary<string, string> { ["professional"] = professional, ["link"] = links.Link($"/professional/{a.ProfessionalId}") });
                break;
        }
    });

    public Task ReviewReceived(ReviewCreated review, string reviewerName) => Safely(() => notifier.NotifyAsync("review_received", review.ProfessionalId, new Dictionary<string, string>
    {
        ["reviewer"] = reviewerName, ["rating"] = review.Rating.ToString(CultureInfo.InvariantCulture), ["link"] = links.Link($"/professional/{review.ProfessionalId}"),
    }));

    /// <summary>Queues a delayed "new message" e-mail (one wait window per conversation and recipient; sent only if still unread when the wait ends).</summary>
    public Task MessageSent(ChatMessage message, string senderName) => Safely(async () =>
    {
        if (settings.GetString("Email.Provider") == "off") return;
        var room = (await chat.RoomsAsync(message.SenderId)).FirstOrDefault(r => r.Id == message.RoomId);
        if (room is null) return;
        var minutes = settings.GetInt("Email.MessageDelayMinutes");
        var now = clock.GetUtcNow();
        var window = now.ToUnixTimeSeconds() / (minutes * 60L);
        await jobs.EnqueueAsync(MessageNotificationJobHandler.JobType,
            new { roomId = message.RoomId, recipientId = room.OtherUserId, sender = senderName, link = links.Link($"/chats/{message.RoomId}") },
            runAtUtc: now.UtcDateTime.AddMinutes(minutes), dedupeKey: $"msg:{message.RoomId}:{room.OtherUserId}:{window}");
    });
}
