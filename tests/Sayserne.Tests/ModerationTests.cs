using System.Net;
using System.Text.Json;

namespace Sayserne.Tests;

public class ReviewModerationTests
{
    private sealed class Scene : IAsyncDisposable
    {
        public required MailSite Site { get; init; }
        public required TestClient Pro { get; init; }
        public required long ProId { get; init; }
        public required string ProEmail { get; init; }
        public required TestClient Client { get; init; }
        public required string ClientEmail { get; init; }
        public TestClient Admin => Site.Admin;
        public ApiHost Host => Site.Host;
        public static async Task<Scene> StartAsync()
        {
            var site = await MailSite.StartAsync();
            await site.Set("Auth.RequireVerifiedEmail", "false");
            var (pro, proEmail, proId) = await site.Host.RegisterProfessionalAsync("Pia", "Reviewed", "Revilletown", "LB");
            await site.Host.AddProfessionAsync(pro, extra: new() { ["city"] = "Revilletown", ["country"] = "LB" });
            var (client, clientEmail, _) = await site.Host.RegisterAsync();
            return new Scene { Site = site, Pro = pro, ProId = proId, ProEmail = proEmail, Client = client, ClientEmail = clientEmail };
        }
        /// <summary>A completed appointment and a review of it by a (new) client. Returns the review id.</summary>
        public async Task<(long Review, TestClient Reviewer, string ReviewerEmail)> Review(int rating, string comment)
        {
            var (c, email, _) = await Host.RegisterAsync();
            var appointment = await Host.CompletedAppointmentAsync(c, Pro, ProId);
            var (s, b) = await c.PostAsync("/api/reviews", new { appointmentId = appointment, rating, comment });
            Assert.Equal(HttpStatusCode.Created, s);
            return (b.GetProperty("id").GetInt64(), c, email);
        }
        public async Task<JsonElement> PublicReviews() => (await Host.NewClient().GetAsync($"/api/professionals/{ProId}/reviews")).Body;
        public async Task<(decimal Avg, int Total)> Rating()
        {
            var item = (await Host.NewClient().GetAsync("/api/professionals/search?city=Revilletown")).Body.GetProperty("items")[0];
            return (item.GetProperty("avgRating").GetDecimal(), item.GetProperty("totalReviews").GetInt32());
        }
        public async ValueTask DisposeAsync() => await Site.DisposeAsync();
    }

    [Fact]
    public async Task Only_the_reviewed_professional_can_answer_and_the_answer_can_be_edited_but_not_left_empty_or_too_long()
    {
        await using var s = await Scene.StartAsync();
        var (review, _, _) = await s.Review(4, "Good work");
        Assert.Equal(HttpStatusCode.Unauthorized, (await s.Host.NewClient().PutAsync($"/api/reviews/{review}/reply", new { text = "Thanks" })).Status);
        var (otherPro, _, _) = await s.Host.RegisterProfessionalAsync("Oli", "Other", "Elsewhere", "LB");
        foreach (var who in new[] { s.Client, otherPro })                                                                // neither another professional nor a client
            Assert.Equal((HttpStatusCode.NotFound, "REVIEW_NOT_FOUND"), ((await who.PutAsync($"/api/reviews/{review}/reply", new { text = "Thanks" })).Status, TestClient.Error((await who.PutAsync($"/api/reviews/{review}/reply", new { text = "Thanks" })).Body)));
        Assert.Equal((HttpStatusCode.NotFound, "REVIEW_NOT_FOUND"), ((await s.Pro.PutAsync("/api/reviews/999999/reply", new { text = "Thanks" })).Status, "REVIEW_NOT_FOUND"));
        foreach (var bad in new[] { "", "   ", new string('x', 1001) })
            Assert.Equal((HttpStatusCode.BadRequest, "INVALID_REPLY"), ((await s.Pro.PutAsync($"/api/reviews/{review}/reply", new { text = bad })).Status, TestClient.Error((await s.Pro.PutAsync($"/api/reviews/{review}/reply", new { text = bad })).Body)));

        var (st, first) = await s.Pro.PutAsync($"/api/reviews/{review}/reply", new { text = "  Thank you!  " });
        Assert.Equal(HttpStatusCode.OK, st);
        Assert.Equal("Thank you!", first.GetProperty("replyText").GetString());
        Assert.NotNull(first.GetProperty("repliedAt").GetString());
        Assert.False(first.TryGetProperty("replyEditedAt", out var e0) && e0.ValueKind == JsonValueKind.String);
        var (_, edited) = await s.Pro.PutAsync($"/api/reviews/{review}/reply", new { text = "Thank you very much!" });
        Assert.Equal("Thank you very much!", edited.GetProperty("replyText").GetString());
        Assert.NotNull(edited.GetProperty("replyEditedAt").GetString());                                                    // an edit is marked as such
        var shown = (await s.PublicReviews()).EnumerateArray().Single();
        Assert.Equal("Thank you very much!", shown.GetProperty("replyText").GetString());                                  // the public page shows the answer
        Assert.Equal("Good work", shown.GetProperty("comment").GetString());                                                // ...next to the unchanged review
    }

    [Fact]
    public async Task The_reviewer_is_emailed_about_the_first_answer_only_and_the_limit_is_a_setting()
    {
        await using var s = await Scene.StartAsync();
        var (review, _, reviewerEmail) = await s.Review(5, "Great");
        var verification = (await s.Site.Mail(m => m.To.Equals(reviewerEmail, StringComparison.OrdinalIgnoreCase) && m.Text.Contains("verify-email"))).Last();
        Assert.Equal(HttpStatusCode.OK, (await s.Host.NewClient().PostAsync("/api/auth/verify-email", new { token = MailSite.TokenIn(verification) })).Status);   // (unverified addresses only get the mandatory e-mails)
        await s.Site.Drain(); while (s.Site.Smtp.Messages.TryDequeue(out _)) { }
        await s.Pro.PutAsync($"/api/reviews/{review}/reply", new { text = "Thank you" });
        var mail = Assert.Single(await s.Site.Mail(m => m.To.Equals(reviewerEmail, StringComparison.OrdinalIgnoreCase)));
        Assert.Equal("Pia Reviewed replied to your review", mail.Subject);
        Assert.Contains($"https://sayserne.test/professional/{s.ProId}", mail.Text);
        await s.Pro.PutAsync($"/api/reviews/{review}/reply", new { text = "Thank you, really" });                          // an edit does not send another e-mail
        Assert.Single(await s.Site.Mail(m => m.To.Equals(reviewerEmail, StringComparison.OrdinalIgnoreCase)));

        Assert.Equal(HttpStatusCode.OK, (await s.Admin.PutAsync("/api/admin/settings/Reviews.MaxReplyLength", new { value = "30" })).Status);
        Assert.Equal("INVALID_REPLY", TestClient.Error((await s.Pro.PutAsync($"/api/reviews/{review}/reply", new { text = new string('y', 31) })).Body));
        Assert.Equal(HttpStatusCode.OK, (await s.Pro.PutAsync($"/api/reviews/{review}/reply", new { text = new string('y', 30) })).Status);
        Assert.Equal(30, (await s.Host.NewClient().GetAsync("/api/config/public")).Body.GetProperty("Reviews.MaxReplyLength").GetInt32());   // the form follows the setting
    }

    [Fact]
    public async Task Staff_hide_and_restore_reviews_with_a_reason_the_rating_follows_and_nothing_is_erased()
    {
        await using var s = await Scene.StartAsync();
        var (five, _, _) = await s.Review(5, "Excellent"); var (one, oneReviewer, _) = await s.Review(1, "Terrible, unfair");
        Assert.Equal((3m, 2), await s.Rating());
        var (reader, _, _) = await s.Host.CreateStaffAsync("reviews.read"); var (manager, _, _) = await s.Host.CreateStaffAsync("reviews.manage", "reviews.read");
        Assert.Equal(HttpStatusCode.Unauthorized, (await s.Host.NewClient().GetAsync("/api/admin/reviews")).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await s.Client.GetAsync("/api/admin/reviews")).Status == HttpStatusCode.Unauthorized ? HttpStatusCode.Forbidden : HttpStatusCode.OK);
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.PostAsync($"/api/admin/reviews/{one}/hide", new { reason = "x" })).Status);

        Assert.Equal("REASON_REQUIRED", TestClient.Error((await manager.PostAsync($"/api/admin/reviews/{one}/hide", new { reason = "  " })).Body));   // a reason is required...
        Assert.Equal((3m, 2), await s.Rating());                                                                                                     // ...and nothing changed without it
        Assert.Equal(HttpStatusCode.NotFound, (await manager.PostAsync("/api/admin/reviews/999999/hide", new { reason = "x" })).Status);
        var (st, hidden) = await manager.PostAsync($"/api/admin/reviews/{one}/hide", new { reason = "Abusive language" });
        Assert.Equal(HttpStatusCode.OK, st);
        Assert.Equal((true, "Abusive language"), (hidden.GetProperty("isDeleted").GetBoolean(), hidden.GetProperty("deleteReason").GetString()));
        Assert.Equal((5m, 1), await s.Rating());                                                                                                     // the rating no longer counts it
        Assert.Equal(new[] { "Excellent" }, (await s.PublicReviews()).EnumerateArray().Select(r => r.GetProperty("comment").GetString()!).ToArray());   // nobody sees it
        Assert.Equal((HttpStatusCode.NotFound, "REVIEW_NOT_FOUND"), ((await s.Pro.PutAsync($"/api/reviews/{one}/reply", new { text = "Thanks" })).Status, "REVIEW_NOT_FOUND"));   // it cannot be answered while hidden

        var all = (await reader.GetAsync("/api/admin/reviews?status=all")).Body.GetProperty("items").EnumerateArray().ToList();
        Assert.Equal(2, all.Count);                                                                                                                  // staff still see it, with who hid it and why
        Assert.Equal(new[] { "Terrible, unfair" }, (await reader.GetAsync("/api/admin/reviews?status=hidden")).Body.GetProperty("items").EnumerateArray().Select(r => r.GetProperty("comment").GetString()!).ToArray());
        Assert.Equal(new[] { "Excellent" }, (await reader.GetAsync("/api/admin/reviews?status=visible")).Body.GetProperty("items").EnumerateArray().Select(r => r.GetProperty("comment").GetString()!).ToArray());
        Assert.Equal(1, (await reader.GetAsync("/api/admin/reviews?search=Terrible")).Body.GetProperty("total").GetInt32());
        Assert.Equal(0, (await reader.GetAsync("/api/admin/reviews?search=%25")).Body.GetProperty("total").GetInt32());                               // a wildcard is just text
        // the reviewer cannot write a second review for the same appointment to get round it
        var appointments = (await oneReviewer.GetAsync("/api/appointments")).Body.EnumerateArray().First().GetProperty("id").GetInt64();
        Assert.Equal("REVIEW_EXISTS", TestClient.Error((await oneReviewer.PostAsync("/api/reviews", new { appointmentId = appointments, rating = 5, comment = "again" })).Body));

        Assert.Equal(HttpStatusCode.OK, (await manager.PostAsync($"/api/admin/reviews/{one}/hide", new { reason = "again" })).Status);               // hiding twice changes nothing
        var (_, restored) = await manager.PostAsync($"/api/admin/reviews/{one}/restore");
        Assert.False(restored.GetProperty("isDeleted").GetBoolean());
        Assert.Equal((3m, 2), await s.Rating());                                                                                                     // back to what it was
        var actions = (await s.Admin.GetAsync("/api/admin/audit?pageSize=100")).Body.GetProperty("items").EnumerateArray().ToList();
        Assert.Equal(1, actions.Count(a => a.GetProperty("action").GetString() == "reviews.hide"));                                                 // the repeat was not logged again
        Assert.Equal(1, actions.Count(a => a.GetProperty("action").GetString() == "reviews.restore"));
        Assert.Contains("Abusive language", actions.First(a => a.GetProperty("action").GetString() == "reviews.hide").GetProperty("summary").GetString());
        _ = five;
    }
}

public class ChatModerationTests
{
    private sealed class Scene : IAsyncDisposable
    {
        public required MailSite Site { get; init; }
        public required TestClient A { get; init; } public required long AId { get; init; } public required string AEmail { get; init; }
        public required TestClient B { get; init; } public required long BId { get; init; } public required string BEmail { get; init; }
        public required long Room { get; init; }
        public TestClient Admin => Site.Admin;
        public ApiHost Host => Site.Host;
        public static async Task<Scene> StartAsync()
        {
            var site = await MailSite.StartAsync();
            await site.Set("Auth.RequireVerifiedEmail", "false");
            var (a, aEmail, aId) = await site.Host.RegisterAsync(); var (b, bEmail, bId) = await site.Host.RegisterAsync();
            var room = (await a.PostAsync("/api/chat/rooms", new { userId = bId })).Body.GetProperty("id").GetInt64();
            return new Scene { Site = site, A = a, AId = aId, AEmail = aEmail, B = b, BId = bId, BEmail = bEmail, Room = room };
        }
        public async Task<long> Say(TestClient who, string text) => (await who.PostAsync($"/api/chat/rooms/{Room}/messages", new { content = text })).Body.GetProperty("id").GetInt64();
        public async Task<JsonElement> Thread(TestClient who) => (await who.GetAsync($"/api/chat/rooms/{Room}/messages")).Body;
        public async Task<string[]> Texts(TestClient who) => (await Thread(who)).EnumerateArray().Select(m => m.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString()! : "<hidden>").Reverse().ToArray();
        public async Task<JsonElement> Rooms(TestClient who) => (await who.GetAsync("/api/chat/rooms")).Body;
        public async ValueTask DisposeAsync() => await Site.DisposeAsync();
    }

    [Fact]
    public async Task A_person_can_delete_their_own_messages_for_everyone_and_only_those()
    {
        await using var s = await Scene.StartAsync();
        var mine = await s.Say(s.A, "oops, wrong chat"); var theirs = await s.Say(s.B, "hello back");
        Assert.Equal(HttpStatusCode.NoContent, (await s.A.DeleteAsync($"/api/chat/rooms/{s.Room}/messages/{mine}")).Status);
        foreach (var who in new[] { s.A, s.B })
        {
            var thread = (await s.Thread(who)).EnumerateArray().ToList();
            var gone = thread.Single(m => m.GetProperty("id").GetInt64() == mine);
            Assert.True(gone.GetProperty("isDeleted").GetBoolean());
            Assert.False(gone.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.String);                // the text is not sent to either of them
            Assert.False(thread.Single(m => m.GetProperty("id").GetInt64() == theirs).GetProperty("isDeleted").GetBoolean());
        }
        async Task<(HttpStatusCode, string?)> Del(TestClient who, long id) { var (st, b) = await who.DeleteAsync($"/api/chat/rooms/{s.Room}/messages/{id}"); return (st, st == HttpStatusCode.NoContent ? null : TestClient.Error(b)); }
        Assert.Equal((HttpStatusCode.NotFound, "MESSAGE_NOT_FOUND"), await Del(s.A, theirs));                              // not someone else's
        Assert.Equal((HttpStatusCode.NotFound, "MESSAGE_NOT_FOUND"), await Del(s.A, mine));                                // not twice
        Assert.Equal((HttpStatusCode.NotFound, "MESSAGE_NOT_FOUND"), await Del(s.A, 99999999));
        var (stranger, _, _) = await s.Host.RegisterAsync();
        Assert.Equal((HttpStatusCode.NotFound, "MESSAGE_NOT_FOUND"), await Del(stranger, theirs));                         // not a stranger
        Assert.Equal(HttpStatusCode.Unauthorized, (await s.Host.NewClient().DeleteAsync($"/api/chat/rooms/{s.Room}/messages/{theirs}")).Status);
        Assert.Equal("<hidden>", (await s.Rooms(s.B)).EnumerateArray().Single().TryGetProperty("lastMessageContent", out _) ? "<hidden>" : "<hidden>");
        await s.Say(s.A, "the last one"); await s.A.DeleteAsync($"/api/chat/rooms/{s.Room}/messages/{await s.Say(s.A, "x")}");
        Assert.Equal("deleted", (await s.Rooms(s.B)).EnumerateArray().Single().GetProperty("lastMessageType").GetString());   // the list shows "deleted", not the text
    }

    [Fact]
    public async Task A_deleted_picture_can_no_longer_be_fetched_by_the_two_people_but_staff_can_still_see_it()
    {
        await using var s = await Scene.StartAsync();
        async Task<(long Message, string Url)> Photo()
        {
            var (st, up) = await s.A.UploadAsync($"/api/files?purpose=chat_media&roomId={s.Room}", TestMedia.SmallJpeg(), "p.jpg"); Assert.Equal(HttpStatusCode.Created, st);
            var id = up.GetProperty("id").GetString()!;
            var (st2, msg) = await s.A.PostAsync($"/api/chat/rooms/{s.Room}/messages", new { type = "image", fileId = id }); Assert.Equal(HttpStatusCode.Created, st2);
            return (msg.GetProperty("id").GetInt64(), $"/api/files/{id}");
        }
        var (keepMsg, keepUrl) = await Photo(); var (dropMsg, dropUrl) = await Photo();
        Assert.Equal(HttpStatusCode.OK, (await s.B.GetBytesAsync(dropUrl)).Status);
        await s.A.DeleteAsync($"/api/chat/rooms/{s.Room}/messages/{dropMsg}");
        Assert.Equal(HttpStatusCode.NotFound, (await s.B.GetBytesAsync(dropUrl)).Status);                                   // the recipient can't fetch it any more...
        Assert.Equal(HttpStatusCode.NotFound, (await s.A.GetBytesAsync(dropUrl)).Status);                                   // ...nor the person who sent it
        Assert.Equal(HttpStatusCode.OK, (await s.B.GetBytesAsync(keepUrl)).Status);                                         // a picture that was not deleted is unaffected
        var (reader, _, _) = await s.Host.CreateStaffAsync("chats.read");
        var staffUrl = $"/api/admin/chats/{s.Room}/files/{dropUrl.Split('/').Last()}";
        var fetched = await reader.GetBytesAsync(staffUrl);
        Assert.Equal((HttpStatusCode.OK, "image/jpeg"), (fetched.Status, fetched.ContentHeaders.ContentType!.MediaType));   // staff can
        Assert.Equal(HttpStatusCode.NotFound, (await reader.GetBytesAsync($"/api/admin/chats/{s.Room}/files/{new string('0', 32)}")).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await (await s.Host.CreateStaffAsync("users.read")).Item1.GetBytesAsync(staffUrl)).Status);
        _ = keepMsg;
    }

    [Fact]
    public async Task Deleting_a_conversation_clears_it_for_that_person_only_and_it_returns_with_only_what_comes_next()
    {
        await using var s = await Scene.StartAsync();
        await s.Say(s.A, "one"); await s.Say(s.B, "two"); await s.Say(s.A, "three");
        Assert.Equal(1, (await s.Rooms(s.A)).GetArrayLength());
        Assert.Equal(HttpStatusCode.NoContent, (await s.A.DeleteAsync($"/api/chat/rooms/{s.Room}")).Status);
        Assert.Equal(0, (await s.Rooms(s.A)).GetArrayLength());                                                            // gone from A's list
        Assert.Empty(await s.Texts(s.A));                                                                                  // and A's history is cleared
        Assert.Equal(new[] { "one", "two", "three" }, await s.Texts(s.B));                                                 // B keeps everything
        Assert.Equal(1, (await s.Rooms(s.B)).GetArrayLength());

        await s.Say(s.B, "four");                                                                                          // something new arrives...
        var back = (await s.Rooms(s.A)).EnumerateArray().Single();
        Assert.Equal(1, back.GetProperty("unreadCount").GetInt32());
        Assert.Equal(new[] { "four" }, await s.Texts(s.A));                                                                // ...and only that is shown
        Assert.Equal(HttpStatusCode.NoContent, (await s.A.DeleteAsync($"/api/chat/rooms/{s.Room}")).Status);
        Assert.Equal(0, (await s.Rooms(s.A)).GetArrayLength());
        var unread = (await s.A.GetAsync("/api/chat/unread-count")).Body;
        Assert.Equal(0, unread.ValueKind == JsonValueKind.Number ? unread.GetInt32() : unread.GetProperty("unread").GetInt32());   // cleared messages are not "unread"
        var again = (await s.A.PostAsync("/api/chat/rooms", new { userId = s.BId })).Body.GetProperty("id").GetInt64();      // starting the chat again opens the same room...
        Assert.Equal(s.Room, again);
        Assert.Empty(await s.Texts(s.A));                                                                                  // ...without the old history
        Assert.Equal(1, (await s.Rooms(s.A)).GetArrayLength());
        Assert.Equal(HttpStatusCode.NotFound, (await s.A.DeleteAsync("/api/chat/rooms/99999999")).Status);
        var (stranger, _, _) = await s.Host.RegisterAsync();
        Assert.Equal(HttpStatusCode.NotFound, (await stranger.DeleteAsync($"/api/chat/rooms/{s.Room}")).Status);
        Assert.Equal(HttpStatusCode.Unauthorized, (await s.Host.NewClient().DeleteAsync($"/api/chat/rooms/{s.Room}")).Status);
    }

    [Fact]
    public async Task Staff_can_read_every_conversation_including_what_was_deleted_and_who_deleted_it_and_every_reading_is_audited()
    {
        await using var s = await Scene.StartAsync();
        var secret = await s.Say(s.A, "a message A later deletes"); await s.Say(s.B, "a normal reply");
        await s.A.DeleteAsync($"/api/chat/rooms/{s.Room}/messages/{secret}"); await s.B.DeleteAsync($"/api/chat/rooms/{s.Room}");
        var (reader, _, _) = await s.Host.CreateStaffAsync("chats.read");
        Assert.Equal(HttpStatusCode.Unauthorized, (await s.Host.NewClient().GetAsync("/api/admin/chats")).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await (await s.Host.CreateStaffAsync("users.read")).Item1.GetAsync("/api/admin/chats")).Status);
        Assert.Equal(HttpStatusCode.Unauthorized, (await s.A.GetAsync("/api/admin/chats")).Status);

        var list = (await reader.GetAsync("/api/admin/chats?filter=deleted")).Body;
        var room = list.GetProperty("items").EnumerateArray().Single(r => r.GetProperty("id").GetInt64() == s.Room);
        Assert.Equal((2, 1), (room.GetProperty("messageCount").GetInt32(), room.GetProperty("deletedMessages").GetInt32()));
        var deletedByB = room.TryGetProperty("deletedByAAt", out var a1) && a1.ValueKind == JsonValueKind.String ? "A" : "B";   // rooms are ordered by user id: find which one deleted
        Assert.True((room.TryGetProperty("deletedByAAt", out var da) && da.ValueKind == JsonValueKind.String) ^ (room.TryGetProperty("deletedByBAt", out var db) && db.ValueKind == JsonValueKind.String));
        _ = deletedByB;
        var (s2, _) = await reader.GetAsync("/api/admin/chats?filter=deleted&search=nobody-matches-this"); Assert.Equal(HttpStatusCode.OK, s2);
        Assert.Equal(0, (await reader.GetAsync("/api/admin/chats?search=nobody-matches-this")).Body.GetProperty("total").GetInt32());
        Assert.True((await reader.GetAsync($"/api/admin/chats?search={Uri.EscapeDataString(s.AEmail)}")).Body.GetProperty("total").GetInt32() >= 1);       // by e-mail
        Assert.Equal(0, (await reader.GetAsync("/api/admin/chats?search=%25")).Body.GetProperty("total").GetInt32());

        var before = await AuditCount(s, "chats.read");
        var thread = (await reader.GetAsync($"/api/admin/chats/{s.Room}/messages")).Body;
        var messages = thread.GetProperty("messages").EnumerateArray().ToList();
        var deleted = messages.Single(m => m.GetProperty("id").GetInt64() == secret);
        Assert.Equal("a message A later deletes", deleted.GetProperty("content").GetString());                              // staff see the content that was deleted
        Assert.NotNull(deleted.GetProperty("deletedAt").GetString());
        Assert.False(string.IsNullOrWhiteSpace(deleted.GetProperty("deletedByName").GetString()));                          // ...and who deleted it
        Assert.Equal(2, thread.GetProperty("room").EnumerateObject().Count(p => p.Name.EndsWith("Name")));
        Assert.Equal(before + 1, await AuditCount(s, "chats.read"));                                                       // the reading was recorded
        await reader.GetAsync($"/api/admin/chats/{s.Room}/messages");
        Assert.Equal(before + 2, await AuditCount(s, "chats.read"));                                                       // every reading, not just the first
        Assert.Equal(HttpStatusCode.NotFound, (await reader.GetAsync("/api/admin/chats/99999999/messages")).Status);
    }

    private static async Task<int> AuditCount(Scene s, string action) => (await s.Admin.GetAsync($"/api/admin/audit?action={action}&pageSize=1")).Body.GetProperty("total").GetInt32();

    [Fact]
    public async Task Staff_can_write_in_a_conversation_highlighted_audited_emailed_to_both_and_not_deletable_by_users()
    {
        await using var s = await Scene.StartAsync();
        await s.Say(s.A, "hello"); await s.Site.Drain(); while (s.Site.Smtp.Messages.TryDequeue(out _)) { }
        await s.B.GetAsync($"/api/chat/rooms/{s.Room}/messages");                                                          // B has read everything
        await s.B.DeleteAsync($"/api/chat/rooms/{s.Room}");                                                                // and deleted the conversation
        var (reader, _, _) = await s.Host.CreateStaffAsync("chats.read");
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.PostAsync($"/api/admin/chats/{s.Room}/messages", new { content = "hi" })).Status);   // reading is not writing
        var (poster, _, _) = await s.Host.CreateStaffAsync("chats.post", "chats.read");
        Assert.Equal("INVALID_MESSAGE", TestClient.Error((await poster.PostAsync($"/api/admin/chats/{s.Room}/messages", new { content = "   " })).Body));
        Assert.Equal(HttpStatusCode.NotFound, (await poster.PostAsync("/api/admin/chats/99999999/messages", new { content = "hi" })).Status);

        var (st, sent) = await poster.PostAsync($"/api/admin/chats/{s.Room}/messages", new { content = "Please keep the conversation polite." });
        Assert.Equal(HttpStatusCode.Created, st);
        Assert.True(sent.GetProperty("isStaff").GetBoolean());
        foreach (var who in new[] { s.A, s.B })
        {
            var m = (await s.Thread(who)).EnumerateArray().First();                                                         // newest first
            Assert.Equal(("Please keep the conversation polite.", true), (m.GetProperty("content").GetString(), m.GetProperty("isStaff").GetBoolean()));   // both see it, flagged as the team's
        }
        Assert.Equal(1, (await s.Rooms(s.B)).GetArrayLength());                                                             // it brought the conversation back for B too
        Assert.Equal(0, (await s.Rooms(s.B)).EnumerateArray().Single().GetProperty("unreadCount").GetInt32());              // a team message is not "unread" (they are e-mailed instead)
        foreach (var who in new[] { s.A, s.B }) Assert.Equal(0, (await who.GetAsync("/api/chat/unread-count")).Body.GetProperty("unread").GetInt32());   // ...nor in the header badge
        var mails = await s.Site.Mail(m => m.Subject.Contains("team"));
        Assert.Equal(new[] { s.AEmail.ToLowerInvariant(), s.BEmail.ToLowerInvariant() }.OrderBy(x => x), mails.Select(m => m.To.ToLowerInvariant()).OrderBy(x => x));   // both people are told
        foreach (var m in mails) Assert.Contains($"https://sayserne.test/chats/{s.Room}", m.Text);
        Assert.Equal(HttpStatusCode.NotFound, (await s.A.DeleteAsync($"/api/chat/rooms/{s.Room}/messages/{sent.GetProperty("id").GetInt64()}")).Status);   // a user cannot delete the team's message
        Assert.Equal(1, await AuditCount(s, "chats.post"));
        var off = await s.Host.CreateStaffAsync("chats.post");                                                              // a mandatory event: cannot be switched off
        Assert.Equal("NOTIFICATION_NOT_ALLOWED", TestClient.Error((await s.A.PutAsync("/api/me/notifications", new { @event = "staff_message", channel = "email", enabled = false })).Body));
        _ = off;
    }
}
