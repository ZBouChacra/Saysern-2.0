using System.Net;
using System.Text.Json;
using MySqlConnector;
using Sayserne.Api;
using Sayserne.Api.Data;

namespace Sayserne.Tests;

public class BookingApiTests
{
    private static object Book(long proId, DateTimeOffset start, int minutes = 60, long? professionId = null, string? note = null) =>
        new { professionalId = proId, professionId, startsAt = start, durationMinutes = minutes, description = note };

    private static async Task<(TestClient Pro, long ProId, TestClient Client, long ClientId)> SetupAsync(ApiHost host)
    {
        var (pro, _, proId) = await host.RegisterProfessionalAsync();
        await host.AddProfessionAsync(pro);
        var (client, _, clientId) = await host.RegisterAsync();
        return (pro, proId, client, clientId);
    }

    [Fact]
    public async Task Booking_requires_a_session()
    {
        await using var host = await ApiHost.StartAsync();
        var anon = host.NewClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.PostAsync("/api/appointments", Book(1, ApiHost.FutureSlot()))).Status);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.GetAsync("/api/appointments")).Status);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.PostAsync("/api/appointments/1/status", new { status = "approved" })).Status);
    }

    [Fact]
    public async Task A_client_can_request_an_appointment_that_starts_as_pending()
    {
        await using var host = await ApiHost.StartAsync();
        var (pro, proId, client, clientId) = await SetupAsync(host);
        var start = ApiHost.FutureSlot();
        var (status, appt) = await client.PostAsync("/api/appointments", Book(proId, start, 90, note: "Contract review"));
        Assert.Equal(HttpStatusCode.Created, status);
        Assert.Equal("pending", ApiHost.Str(appt, "status"));
        Assert.Equal(clientId, appt.GetProperty("clientId").GetInt64());
        Assert.Equal(90, appt.GetProperty("durationMinutes").GetInt32());
        Assert.Equal(start.UtcDateTime, appt.GetProperty("startsAt").GetDateTime().ToUniversalTime());
        Assert.Equal(start.UtcDateTime.AddMinutes(90), appt.GetProperty("endsAt").GetDateTime().ToUniversalTime());
        // both parties see it
        Assert.Equal(1, (await client.GetAsync("/api/appointments")).Body.GetArrayLength());
        Assert.Equal(1, (await pro.GetAsync("/api/appointments")).Body.GetArrayLength());
    }

    [Fact]
    public async Task Appointments_are_only_listed_for_their_two_participants()
    {
        await using var host = await ApiHost.StartAsync();
        var (_, proId, client, _) = await SetupAsync(host);
        await client.PostAsync("/api/appointments", Book(proId, ApiHost.FutureSlot()));
        var (outsider, _, _) = await host.RegisterAsync();
        Assert.Equal(0, (await outsider.GetAsync("/api/appointments")).Body.GetArrayLength());
    }

    [Fact]
    public async Task Bookings_in_the_past_or_with_silly_durations_are_refused()
    {
        await using var host = await ApiHost.StartAsync();
        var (_, proId, client, _) = await SetupAsync(host);
        var (s1, b1) = await client.PostAsync("/api/appointments", Book(proId, DateTimeOffset.UtcNow.AddHours(-3)));
        Assert.Equal(HttpStatusCode.BadRequest, s1);
        Assert.Equal("START_IN_PAST", TestClient.Error(b1));
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync("/api/appointments", Book(proId, ApiHost.FutureSlot(), 5))).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync("/api/appointments", Book(proId, ApiHost.FutureSlot(), 9999))).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync("/api/appointments", new { professionalId = proId })).Status);   // no start time
    }

    [Fact]
    public async Task You_cannot_book_yourself_a_customer_or_a_nobody()
    {
        await using var host = await ApiHost.StartAsync();
        var (pro, proId, client, clientId) = await SetupAsync(host);
        var (s1, b1) = await pro.PostAsync("/api/appointments", Book(proId, ApiHost.FutureSlot()));
        Assert.Equal(HttpStatusCode.BadRequest, s1);
        Assert.Equal("CANNOT_BOOK_SELF", TestClient.Error(b1));
        var (s2, b2) = await pro.PostAsync("/api/appointments", Book(clientId, ApiHost.FutureSlot()));   // a customer is not bookable
        Assert.Equal(HttpStatusCode.NotFound, s2);
        Assert.Equal("PROFESSIONAL_NOT_FOUND", TestClient.Error(b2));
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsync("/api/appointments", Book(99999999, ApiHost.FutureSlot()))).Status);
    }

    [Fact]
    public async Task Hidden_listings_and_other_peoples_listings_cannot_be_booked()
    {
        await using var host = await ApiHost.StartAsync();
        var (pro, proId, client, _) = await SetupAsync(host);
        var (other, _, _) = await host.RegisterProfessionalAsync();
        var othersListing = await host.AddProfessionAsync(other);
        var mine = (await pro.GetAsync("/api/me/professions")).Body[0].GetProperty("id").GetInt64();
        var (s1, b1) = await client.PostAsync("/api/appointments", Book(proId, ApiHost.FutureSlot(), professionId: othersListing));
        Assert.Equal(HttpStatusCode.NotFound, s1);
        Assert.Equal("PROFESSION_NOT_FOUND", TestClient.Error(b1));
        Assert.Equal(HttpStatusCode.Created, (await client.PostAsync("/api/appointments", Book(proId, ApiHost.FutureSlot(), professionId: mine))).Status);
        await pro.PutAsync($"/api/me/professions/{mine}", new { isHidden = true });
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsync("/api/appointments", Book(proId, ApiHost.FutureSlot(), professionId: mine))).Status);
    }

    [Fact]
    public async Task Overlapping_bookings_are_refused_but_back_to_back_ones_are_fine()
    {
        await using var host = await ApiHost.StartAsync();
        var (_, proId, client, _) = await SetupAsync(host);
        var (other, _, _) = await host.RegisterAsync();
        var ten = ApiHost.FutureSlot(10);
        Assert.Equal(HttpStatusCode.Created, (await client.PostAsync("/api/appointments", Book(proId, ten, 60))).Status);              // 10:00-11:00
        var (s1, b1) = await other.PostAsync("/api/appointments", Book(proId, ten.AddMinutes(30), 60));                                 // 10:30-11:30
        Assert.Equal(HttpStatusCode.Conflict, s1);
        Assert.Equal("SLOT_TAKEN", TestClient.Error(b1));
        Assert.Equal(HttpStatusCode.Conflict, (await other.PostAsync("/api/appointments", Book(proId, ten.AddMinutes(-30), 60))).Status);   // 09:30-10:30
        Assert.Equal(HttpStatusCode.Conflict, (await other.PostAsync("/api/appointments", Book(proId, ten.AddMinutes(10), 15))).Status);    // fully inside
        Assert.Equal(HttpStatusCode.Created, (await other.PostAsync("/api/appointments", Book(proId, ten.AddHours(1), 60))).Status);        // 11:00-12:00 touches, no overlap
        Assert.Equal(HttpStatusCode.Created, (await other.PostAsync("/api/appointments", Book(proId, ten.AddHours(-1), 60))).Status);       // 09:00-10:00
    }

    [Fact]
    public async Task A_cancelled_appointment_frees_its_slot()
    {
        await using var host = await ApiHost.StartAsync();
        var (_, proId, client, _) = await SetupAsync(host);
        var (other, _, _) = await host.RegisterAsync();
        var slot = ApiHost.FutureSlot();
        var id = (await client.PostAsync("/api/appointments", Book(proId, slot))).Body.GetProperty("id").GetInt64();
        Assert.Equal(HttpStatusCode.Conflict, (await other.PostAsync("/api/appointments", Book(proId, slot))).Status);
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsync($"/api/appointments/{id}/status", new { status = "cancelled" })).Status);
        Assert.Equal(HttpStatusCode.Created, (await other.PostAsync("/api/appointments", Book(proId, slot))).Status);
    }

    [Fact]
    public async Task Twelve_people_racing_for_the_same_slot_produce_exactly_one_booking()
    {
        await using var host = await ApiHost.StartAsync();
        var (pro, proId, _, _) = await SetupAsync(host);
        var slot = ApiHost.FutureSlot();
        var clients = new List<TestClient>();
        for (var i = 0; i < 12; i++) clients.Add((await host.RegisterAsync()).Client);
        var results = await Task.WhenAll(clients.Select(c => c.PostAsync("/api/appointments", Book(proId, slot))));
        Assert.Equal(1, results.Count(r => r.Status == HttpStatusCode.Created));
        Assert.Equal(11, results.Count(r => r.Status == HttpStatusCode.Conflict));
        Assert.Equal(1, (await pro.GetAsync("/api/appointments")).Body.GetArrayLength());
    }

    [Fact]
    public async Task The_busy_calendar_is_public_but_never_says_who_booked_or_why()
    {
        await using var host = await ApiHost.StartAsync();
        var (_, proId, client, clientId) = await SetupAsync(host);
        var slot = ApiHost.FutureSlot();
        await client.PostAsync("/api/appointments", Book(proId, slot, 60, note: "secret reason"));
        var (status, busy) = await host.NewClient().GetAsync($"/api/professionals/{proId}/busy");   // anonymous
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(1, busy.GetArrayLength());
        var text = busy.GetRawText();
        Assert.False(text.Contains("secret reason") || text.Contains($"\"clientId\"") || text.Contains(clientId.ToString() + ","));
        Assert.Equal(slot.UtcDateTime, busy[0].GetProperty("startsAt").GetDateTime().ToUniversalTime());
        Assert.Equal(HttpStatusCode.NotFound, (await host.NewClient().GetAsync("/api/professionals/99999999/busy")).Status);
    }

    // ---- state machine ----

    [Fact]
    public async Task Only_the_professional_can_approve_and_a_client_cannot_approve_their_own_booking()
    {
        await using var host = await ApiHost.StartAsync();
        var (pro, proId, client, _) = await SetupAsync(host);
        var id = (await client.PostAsync("/api/appointments", Book(proId, ApiHost.FutureSlot()))).Body.GetProperty("id").GetInt64();
        var (s1, b1) = await client.PostAsync($"/api/appointments/{id}/status", new { status = "approved" });
        Assert.Equal(HttpStatusCode.Forbidden, s1);
        Assert.Equal("FORBIDDEN_ACTION", TestClient.Error(b1));
        var (s2, ok) = await pro.PostAsync($"/api/appointments/{id}/status", new { status = "approved" });
        Assert.Equal(HttpStatusCode.OK, s2);
        Assert.Equal("approved", ApiHost.Str(ok, "status"));
    }

    [Fact]
    public async Task A_client_cannot_mark_an_appointment_completed_to_unlock_a_review()
    {
        await using var host = await ApiHost.StartAsync();
        var (pro, proId, client, _) = await SetupAsync(host);
        var id = (await client.PostAsync("/api/appointments", Book(proId, ApiHost.FutureSlot()))).Body.GetProperty("id").GetInt64();
        await pro.PostAsync($"/api/appointments/{id}/status", new { status = "approved" });
        await host.AddProfessionAsync(pro, "Family Law");   // irrelevant noise
        var (status, body) = await client.PostAsync($"/api/appointments/{id}/status", new { status = "completed" });
        Assert.Equal(HttpStatusCode.Forbidden, status);
        Assert.Equal("FORBIDDEN_ACTION", TestClient.Error(body));
        var (rs, _) = await client.PostAsync("/api/reviews", new { appointmentId = id, rating = 5 });
        Assert.Equal(HttpStatusCode.Conflict, rs);   // still not completed
    }

    [Fact]
    public async Task An_appointment_cannot_be_completed_before_it_starts()
    {
        await using var host = await ApiHost.StartAsync();
        var (pro, proId, client, _) = await SetupAsync(host);
        var id = (await client.PostAsync("/api/appointments", Book(proId, ApiHost.FutureSlot()))).Body.GetProperty("id").GetInt64();
        await pro.PostAsync($"/api/appointments/{id}/status", new { status = "approved" });
        var (status, body) = await pro.PostAsync($"/api/appointments/{id}/status", new { status = "completed" });
        Assert.Equal(HttpStatusCode.Conflict, status);
        Assert.Equal("NOT_STARTED_YET", TestClient.Error(body));
    }

    [Fact]
    public async Task Pending_appointments_cannot_skip_approval_and_final_states_are_final()
    {
        await using var host = await ApiHost.StartAsync();
        var (pro, proId, client, _) = await SetupAsync(host);
        var id = (await client.PostAsync("/api/appointments", Book(proId, ApiHost.FutureSlot()))).Body.GetProperty("id").GetInt64();
        Assert.Equal("INVALID_TRANSITION", TestClient.Error((await pro.PostAsync($"/api/appointments/{id}/status", new { status = "completed" })).Body));
        await pro.PostAsync($"/api/appointments/{id}/status", new { status = "cancelled" });
        Assert.Equal("INVALID_TRANSITION", TestClient.Error((await pro.PostAsync($"/api/appointments/{id}/status", new { status = "approved" })).Body));
        Assert.Equal("INVALID_TRANSITION", TestClient.Error((await client.PostAsync($"/api/appointments/{id}/status", new { status = "cancelled" })).Body));
    }

    [Fact]
    public async Task Both_parties_can_cancel_an_approved_appointment()
    {
        await using var host = await ApiHost.StartAsync();
        var (pro, proId, client, _) = await SetupAsync(host);
        var a = (await client.PostAsync("/api/appointments", Book(proId, ApiHost.FutureSlot()))).Body.GetProperty("id").GetInt64();
        var b = (await client.PostAsync("/api/appointments", Book(proId, ApiHost.FutureSlot()))).Body.GetProperty("id").GetInt64();
        await pro.PostAsync($"/api/appointments/{a}/status", new { status = "approved" });
        await pro.PostAsync($"/api/appointments/{b}/status", new { status = "approved" });
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsync($"/api/appointments/{a}/status", new { status = "cancelled" })).Status);
        Assert.Equal(HttpStatusCode.OK, (await pro.PostAsync($"/api/appointments/{b}/status", new { status = "cancelled" })).Status);
    }

    [Fact]
    public async Task Outsiders_cannot_touch_an_appointment_and_cannot_even_tell_it_exists()
    {
        await using var host = await ApiHost.StartAsync();
        var (_, proId, client, _) = await SetupAsync(host);
        var id = (await client.PostAsync("/api/appointments", Book(proId, ApiHost.FutureSlot()))).Body.GetProperty("id").GetInt64();
        var (outsider, _, _) = await host.RegisterAsync();
        var (s1, b1) = await outsider.PostAsync($"/api/appointments/{id}/status", new { status = "cancelled" });
        Assert.Equal(HttpStatusCode.NotFound, s1);
        Assert.Equal("APPOINTMENT_NOT_FOUND", TestClient.Error(b1));
        Assert.Equal(HttpStatusCode.NotFound, (await outsider.PostAsync("/api/appointments/99999999/status", new { status = "cancelled" })).Status);   // same answer for a missing id
        Assert.Equal("INVALID_STATUS", TestClient.Error((await client.PostAsync($"/api/appointments/{id}/status", new { status = "teleported" })).Body));
    }

    [Fact]
    public async Task Booking_writes_are_rate_limited_per_user()
    {
        await using var host = await ApiHost.StartAsync(new() { ["Auth:WritesPerMinute"] = "3" });
        var (_, proId, client, _) = await SetupAsync(host);
        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 6; i++) statuses.Add((await client.PostAsync("/api/appointments", Book(proId, ApiHost.FutureSlot()))).Status);
        Assert.True(statuses.Contains(HttpStatusCode.TooManyRequests), string.Join(",", statuses));
    }
}

public class BookingConcurrencyTests
{
    [Fact]
    public async Task Forty_simultaneous_database_calls_for_one_slot_produce_exactly_one_appointment_in_every_round()
    {
        // Straight at the stored procedure (no HTTP in between) so the calls genuinely overlap in the database.
        await using var host = await ApiHost.StartAsync();
        var (pro, _, proId) = await host.RegisterProfessionalAsync();
        await host.AddProfessionAsync(pro);
        var (_, _, clientId) = await host.RegisterAsync();
        var db = await TestEnv.GetDatabaseAsync();
        await using var pool = new MySqlDataSource(db.AppConnection + ";Maximum Pool Size=100");   // disposed at the end of the test
        var repo = new BookingRepository(new StoredProcedureExecutor(pool));
        for (var round = 0; round < 3; round++)   // a race is probabilistic: three independent rounds make a missing lock all but certain to show
        {
            var slot = ApiHost.FutureSlot().UtcDateTime;
            var gate = new TaskCompletionSource();
            var calls = Enumerable.Range(0, 40).Select(_ => Task.Run(async () =>
            {
                await gate.Task;
                try { await repo.CreateAppointmentAsync(clientId, proId, null, slot, 60, null); return true; }
                catch (AppException e) when (e.Code == "SLOT_TAKEN") { return false; }
            })).ToList();
            await Task.Delay(300);   // let every task reach the gate (and open its connection)
            gate.SetResult();
            var results = await Task.WhenAll(calls);
            Assert.Equal(1, results.Count(r => r));
        }
        Assert.Equal(3, (await pro.GetAsync("/api/appointments")).Body.GetArrayLength());   // exactly one per round
    }
}

public class ReviewApiTests
{
    [Fact]
    public async Task A_client_can_review_a_completed_appointment_and_the_rating_shows_up_everywhere()
    {
        await using var host = await ApiHost.StartAsync();
        var tag = "Rv" + Guid.NewGuid().ToString("N")[..8];
        var (pro, _, proId) = await host.RegisterProfessionalAsync("Rev", "Iewed", tag);
        var professionId = await host.AddProfessionAsync(pro, extra: new() { ["city"] = tag });
        var (client, _, _) = await host.RegisterAsync();
        var appt = await host.CompletedAppointmentAsync(client, pro, proId, professionId);

        var (status, review) = await client.PostAsync("/api/reviews", new { appointmentId = appt, rating = 4, comment = "  Very helpful  " });
        Assert.Equal(HttpStatusCode.Created, status);
        Assert.Equal(proId, review.GetProperty("professionalId").GetInt64());

        var (_, list) = await host.NewClient().GetAsync($"/api/professionals/{proId}/reviews");   // public
        Assert.Equal(1, list.GetArrayLength());
        Assert.Equal("Very helpful", ApiHost.Str(list[0], "comment"));
        Assert.Equal("Test", ApiHost.Str(list[0], "reviewerFirstName"));
        Assert.False(list.GetRawText().Contains("@"), "reviewer email leaked");

        var item = (await host.NewClient().GetAsync($"/api/professionals/search?city={tag}")).Body.GetProperty("items")[0];
        Assert.Equal(4m, item.GetProperty("avgRating").GetDecimal());
        Assert.Equal(1, item.GetProperty("totalReviews").GetInt32());
        Assert.Equal(1, (await host.NewClient().GetAsync($"/api/professionals/search?city={tag}&minStars=4")).Body.GetProperty("total").GetInt32());
        Assert.Equal(0, (await host.NewClient().GetAsync($"/api/professionals/search?city={tag}&minStars=5")).Body.GetProperty("total").GetInt32());
    }

    [Fact]
    public async Task The_average_covers_all_reviews()
    {
        await using var host = await ApiHost.StartAsync();
        var tag = "Av" + Guid.NewGuid().ToString("N")[..8];
        var (pro, _, proId) = await host.RegisterProfessionalAsync("Avg", "Pro", tag);
        var professionId = await host.AddProfessionAsync(pro, extra: new() { ["city"] = tag });
        foreach (var rating in new[] { 5, 3, 4 })
        {
            var (client, _, _) = await host.RegisterAsync();
            var appt = await host.CompletedAppointmentAsync(client, pro, proId, professionId);
            Assert.Equal(HttpStatusCode.Created, (await client.PostAsync("/api/reviews", new { appointmentId = appt, rating })).Status);
        }
        var item = (await host.NewClient().GetAsync($"/api/professionals/search?city={tag}")).Body.GetProperty("items")[0];
        Assert.Equal(4m, item.GetProperty("avgRating").GetDecimal());
        Assert.Equal(3, item.GetProperty("totalReviews").GetInt32());
    }

    [Fact]
    public async Task An_appointment_can_be_reviewed_only_once()
    {
        await using var host = await ApiHost.StartAsync();
        var (pro, _, proId) = await host.RegisterProfessionalAsync();
        await host.AddProfessionAsync(pro);
        var (client, _, _) = await host.RegisterAsync();
        var appt = await host.CompletedAppointmentAsync(client, pro, proId);
        Assert.Equal(HttpStatusCode.Created, (await client.PostAsync("/api/reviews", new { appointmentId = appt, rating = 5 })).Status);
        var (status, body) = await client.PostAsync("/api/reviews", new { appointmentId = appt, rating = 1 });
        Assert.Equal(HttpStatusCode.Conflict, status);
        Assert.Equal("REVIEW_EXISTS", TestClient.Error(body));
    }

    [Fact]
    public async Task You_cannot_review_someone_elses_appointment_even_with_your_own_completed_booking()
    {
        await using var host = await ApiHost.StartAsync();
        var (pro, _, proId) = await host.RegisterProfessionalAsync();
        await host.AddProfessionAsync(pro);
        var (alice, _, _) = await host.RegisterAsync();
        var (mallory, _, _) = await host.RegisterAsync();
        await host.CompletedAppointmentAsync(mallory, pro, proId);          // Mallory has a completed booking with this professional...
        var aliceAppt = await host.CompletedAppointmentAsync(alice, pro, proId);
        var (status, body) = await mallory.PostAsync("/api/reviews", new { appointmentId = aliceAppt, rating = 1, comment = "spam" });   // ...but cannot review Alice's
        Assert.Equal(HttpStatusCode.NotFound, status);
        Assert.Equal("APPOINTMENT_NOT_FOUND", TestClient.Error(body));
        Assert.Equal(0, (await host.NewClient().GetAsync($"/api/professionals/{proId}/reviews")).Body.GetArrayLength());
    }

    [Fact]
    public async Task The_professional_cannot_review_their_own_appointment()
    {
        await using var host = await ApiHost.StartAsync();
        var (pro, _, proId) = await host.RegisterProfessionalAsync();
        await host.AddProfessionAsync(pro);
        var (client, _, _) = await host.RegisterAsync();
        var appt = await host.CompletedAppointmentAsync(client, pro, proId);
        Assert.Equal(HttpStatusCode.NotFound, (await pro.PostAsync("/api/reviews", new { appointmentId = appt, rating = 5 })).Status);
    }

    [Fact]
    public async Task Only_completed_appointments_can_be_reviewed()
    {
        await using var host = await ApiHost.StartAsync();
        var (pro, _, proId) = await host.RegisterProfessionalAsync();
        await host.AddProfessionAsync(pro);
        var (client, _, _) = await host.RegisterAsync();
        var id = (await client.PostAsync("/api/appointments", new { professionalId = proId, startsAt = ApiHost.FutureSlot() })).Body.GetProperty("id").GetInt64();
        var (status, body) = await client.PostAsync("/api/reviews", new { appointmentId = id, rating = 5 });
        Assert.Equal(HttpStatusCode.Conflict, status);
        Assert.Equal("NOT_COMPLETED", TestClient.Error(body));
    }

    [Fact]
    public async Task Ratings_outside_one_to_five_and_oversized_comments_are_rejected()
    {
        await using var host = await ApiHost.StartAsync();
        var (pro, _, proId) = await host.RegisterProfessionalAsync();
        await host.AddProfessionAsync(pro);
        var (client, _, _) = await host.RegisterAsync();
        var appt = await host.CompletedAppointmentAsync(client, pro, proId);
        foreach (var rating in new[] { 0, 6, -1, 100 })
            Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync("/api/reviews", new { appointmentId = appt, rating })).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync("/api/reviews", new { appointmentId = appt, rating = 5, comment = new string('x', 2001) })).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync("/api/reviews", new { rating = 5 })).Status);
        Assert.Equal(HttpStatusCode.Created, (await client.PostAsync("/api/reviews", new { appointmentId = appt, rating = 5 })).Status);   // still reviewable after bad attempts
    }

    [Fact]
    public async Task The_eligible_endpoint_lists_only_my_completed_unreviewed_appointments()
    {
        await using var host = await ApiHost.StartAsync();
        var (pro, _, proId) = await host.RegisterProfessionalAsync();
        await host.AddProfessionAsync(pro);
        var (client, _, _) = await host.RegisterAsync();
        var (other, _, _) = await host.RegisterAsync();
        var a = await host.CompletedAppointmentAsync(client, pro, proId);
        var b = await host.CompletedAppointmentAsync(client, pro, proId);
        await host.CompletedAppointmentAsync(other, pro, proId);
        await client.PostAsync("/api/reviews", new { appointmentId = a, rating = 5 });
        var (_, eligible) = await client.GetAsync($"/api/reviews/eligible?professionalId={proId}");
        Assert.Equal(1, eligible.GetArrayLength());
        Assert.Equal(b, eligible[0].GetProperty("id").GetInt64());
        Assert.Equal(HttpStatusCode.Unauthorized, (await host.NewClient().GetAsync($"/api/reviews/eligible?professionalId={proId}")).Status);
    }

    [Fact]
    public async Task Reviews_require_a_session()
    {
        await using var host = await ApiHost.StartAsync();
        Assert.Equal(HttpStatusCode.Unauthorized, (await host.NewClient().PostAsync("/api/reviews", new { appointmentId = 1, rating = 5 })).Status);
    }
}

public class ChatApiTests
{
    private static async Task<long> RoomAsync(TestClient from, long otherId) =>
        (await from.PostAsync("/api/chat/rooms", new { userId = otherId })).Body.GetProperty("id").GetInt64();

    [Fact]
    public async Task Chat_requires_a_session()
    {
        await using var host = await ApiHost.StartAsync();
        var anon = host.NewClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.GetAsync("/api/chat/rooms")).Status);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.PostAsync("/api/chat/rooms", new { userId = 1 })).Status);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.GetAsync("/api/chat/rooms/1/messages")).Status);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.PostAsync("/api/chat/rooms/1/messages", new { content = "hi" })).Status);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.GetAsync("/api/chat/unread-count")).Status);
    }

    [Fact]
    public async Task Starting_a_chat_gives_the_same_room_whoever_starts_it()
    {
        await using var host = await ApiHost.StartAsync();
        var (a, _, aId) = await host.RegisterAsync();
        var (b, _, bId) = await host.RegisterAsync();
        var r1 = await RoomAsync(a, bId);
        Assert.Equal(r1, await RoomAsync(a, bId));
        Assert.Equal(r1, await RoomAsync(b, aId));
    }

    [Fact]
    public async Task You_cannot_chat_with_yourself_an_unknown_user_or_a_locked_user()
    {
        await using var host = await ApiHost.StartAsync();
        var (a, _, aId) = await host.RegisterAsync();
        var (_, _, lockedId) = await host.RegisterAsync();
        var (admin, _, _) = await host.CreateAdminAsync();
        var (s1, b1) = await a.PostAsync("/api/chat/rooms", new { userId = aId });
        Assert.Equal(HttpStatusCode.BadRequest, s1);
        Assert.Equal("CANNOT_CHAT_SELF", TestClient.Error(b1));
        Assert.Equal(HttpStatusCode.NotFound, (await a.PostAsync("/api/chat/rooms", new { userId = 99999999 })).Status);
        await admin.PostAsync($"/api/admin/users/{lockedId}/lock", new { isLocked = true });
        Assert.Equal(HttpStatusCode.NotFound, (await a.PostAsync("/api/chat/rooms", new { userId = lockedId })).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await a.PostAsync("/api/chat/rooms", new { })).Status);
    }

    [Fact]
    public async Task Messages_flow_both_ways_newest_first()
    {
        await using var host = await ApiHost.StartAsync();
        var (a, _, aId) = await host.RegisterAsync();
        var (b, _, bId) = await host.RegisterAsync();
        var room = await RoomAsync(a, bId);
        Assert.Equal(HttpStatusCode.Created, (await a.PostAsync($"/api/chat/rooms/{room}/messages", new { content = "  Hello  " })).Status);
        Assert.Equal(HttpStatusCode.Created, (await b.PostAsync($"/api/chat/rooms/{room}/messages", new { content = "Hi back" })).Status);
        var (_, messages) = await a.GetAsync($"/api/chat/rooms/{room}/messages");
        Assert.Equal(2, messages.GetArrayLength());
        Assert.Equal("Hi back", ApiHost.Str(messages[0], "content"));
        Assert.Equal("Hello", ApiHost.Str(messages[1], "content"));
        Assert.Equal(aId, messages[1].GetProperty("senderId").GetInt64());
    }

    [Fact]
    public async Task Strangers_cannot_read_write_or_mark_read_in_a_room_they_are_not_in()
    {
        await using var host = await ApiHost.StartAsync();
        var (a, _, _) = await host.RegisterAsync();
        var (b, _, bId) = await host.RegisterAsync();
        var (eve, _, _) = await host.RegisterAsync();
        var room = await RoomAsync(a, bId);
        await a.PostAsync($"/api/chat/rooms/{room}/messages", new { content = "private" });

        var (s1, b1) = await eve.GetAsync($"/api/chat/rooms/{room}/messages");
        Assert.Equal(HttpStatusCode.NotFound, s1);
        Assert.Equal("ROOM_NOT_FOUND", TestClient.Error(b1));
        Assert.Equal(HttpStatusCode.NotFound, (await eve.PostAsync($"/api/chat/rooms/{room}/messages", new { content = "injected" })).Status);
        Assert.Equal(0, (await eve.GetAsync("/api/chat/rooms")).Body.GetArrayLength());
        Assert.Equal(0, (await eve.GetAsync("/api/chat/unread-count")).Body.GetProperty("unread").GetInt32());

        // the real participants are unaffected: b still has the message unread (Eve's read attempt did not mark it)
        Assert.Equal(1, (await b.GetAsync("/api/chat/unread-count")).Body.GetProperty("unread").GetInt32());
        Assert.Equal(1, (await a.GetAsync($"/api/chat/rooms/{room}/messages")).Body.GetArrayLength());
    }

    [Fact]
    public async Task Unread_counts_and_read_marking_work_per_user()
    {
        await using var host = await ApiHost.StartAsync();
        var (a, _, _) = await host.RegisterAsync();
        var (b, _, bId) = await host.RegisterAsync();
        var room = await RoomAsync(a, bId);
        for (var i = 0; i < 3; i++) await a.PostAsync($"/api/chat/rooms/{room}/messages", new { content = $"m{i}" });
        Assert.Equal(3, (await b.GetAsync("/api/chat/unread-count")).Body.GetProperty("unread").GetInt32());
        Assert.Equal(0, (await a.GetAsync("/api/chat/unread-count")).Body.GetProperty("unread").GetInt32());   // own messages are never "unread"

        var summary = (await b.GetAsync("/api/chat/rooms")).Body[0];
        Assert.Equal(3, summary.GetProperty("unreadCount").GetInt32());
        Assert.Equal("m2", ApiHost.Str(summary, "lastMessageContent"));

        await b.GetAsync($"/api/chat/rooms/{room}/messages");   // opening the room marks them read
        Assert.Equal(0, (await b.GetAsync("/api/chat/unread-count")).Body.GetProperty("unread").GetInt32());
        Assert.Equal(0, (await b.GetAsync("/api/chat/rooms")).Body[0].GetProperty("unreadCount").GetInt32());
    }

    [Fact]
    public async Task Rooms_are_listed_most_recently_active_first_with_the_other_persons_name()
    {
        await using var host = await ApiHost.StartAsync();
        var (a, _, _) = await host.RegisterAsync();
        var (_, _, b) = await host.RegisterAsync();
        var (_, _, c) = await host.RegisterAsync();
        var r1 = await RoomAsync(a, b);
        var r2 = await RoomAsync(a, c);
        await Task.Delay(30);
        await a.PostAsync($"/api/chat/rooms/{r1}/messages", new { content = "bump" });
        var rooms = (await a.GetAsync("/api/chat/rooms")).Body;
        Assert.Equal(r1, rooms[0].GetProperty("id").GetInt64());
        Assert.Equal(r2, rooms[1].GetProperty("id").GetInt64());
        Assert.Equal("Test", ApiHost.Str(rooms[0], "otherFirstName"));
    }

    [Fact]
    public async Task Message_content_is_validated()
    {
        await using var host = await ApiHost.StartAsync();
        var (a, _, _) = await host.RegisterAsync();
        var (_, _, bId) = await host.RegisterAsync();
        var room = await RoomAsync(a, bId);
        foreach (var bad in new object[]
        {
            new { content = "" }, new { content = "   " }, new { content = new string('x', 4001) },
            new { type = "image", content = "x" }, new { type = "location", latitude = 91, longitude = 10 },
            new { type = "location", latitude = 10, longitude = 181 }, new { type = "location" },
        })
        {
            var (status, body) = await a.PostAsync($"/api/chat/rooms/{room}/messages", bad);
            Assert.Equal(HttpStatusCode.BadRequest, status);
            Assert.Equal("VALIDATION_FAILED", TestClient.Error(body));
        }
        Assert.Equal(0, (await a.GetAsync($"/api/chat/rooms/{room}/messages")).Body.GetArrayLength());
    }

    [Fact]
    public async Task A_location_can_be_shared()
    {
        await using var host = await ApiHost.StartAsync();
        var (a, _, _) = await host.RegisterAsync();
        var (b, _, bId) = await host.RegisterAsync();
        var room = await RoomAsync(a, bId);
        var (status, sent) = await a.PostAsync($"/api/chat/rooms/{room}/messages", new { type = "location", latitude = 33.8938, longitude = 35.5018 });
        Assert.Equal(HttpStatusCode.Created, status);
        Assert.Equal("location", ApiHost.Str(sent, "messageType"));
        var msg = (await b.GetAsync($"/api/chat/rooms/{room}/messages")).Body[0];
        Assert.Equal(33.8938m, msg.GetProperty("latitude").GetDecimal());
        Assert.Equal(35.5018m, msg.GetProperty("longitude").GetDecimal());
    }

    [Fact]
    public async Task Message_history_can_be_paged()
    {
        await using var host = await ApiHost.StartAsync();
        var (a, _, _) = await host.RegisterAsync();
        var (_, _, bId) = await host.RegisterAsync();
        var room = await RoomAsync(a, bId);
        for (var i = 0; i < 5; i++) await a.PostAsync($"/api/chat/rooms/{room}/messages", new { content = $"m{i}" });
        var page1 = (await a.GetAsync($"/api/chat/rooms/{room}/messages?limit=2&offset=0")).Body;
        var page2 = (await a.GetAsync($"/api/chat/rooms/{room}/messages?limit=2&offset=2")).Body;
        Assert.Equal(new[] { "m4", "m3" }, page1.EnumerateArray().Select(m => ApiHost.Str(m, "content")).ToArray());
        Assert.Equal(new[] { "m2", "m1" }, page2.EnumerateArray().Select(m => ApiHost.Str(m, "content")).ToArray());
    }

    [Fact]
    public async Task Sending_messages_is_rate_limited()
    {
        await using var host = await ApiHost.StartAsync(new() { ["Auth:WritesPerMinute"] = "4" });
        var (a, _, _) = await host.RegisterAsync();
        var (_, _, bId) = await host.RegisterAsync();
        var room = await RoomAsync(a, bId);   // 1 write
        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 6; i++) statuses.Add((await a.PostAsync($"/api/chat/rooms/{room}/messages", new { content = "spam" })).Status);
        Assert.True(statuses.Contains(HttpStatusCode.TooManyRequests), string.Join(",", statuses));
    }
}

public class AlertApiTests
{
    [Fact]
    public async Task Alerts_require_a_session()
    {
        await using var host = await ApiHost.StartAsync();
        var anon = host.NewClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.GetAsync("/api/alerts")).Status);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.PostAsync("/api/alerts", new { name = "x", searchCriteria = new { } })).Status);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.DeleteAsync("/api/alerts/1")).Status);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.GetAsync("/api/alerts/1/run")).Status);
    }

    [Fact]
    public async Task An_alert_stores_its_criteria_and_re_runs_the_search()
    {
        await using var host = await ApiHost.StartAsync();
        var tag = "Al" + Guid.NewGuid().ToString("N")[..8];
        var (pro, _, _) = await host.RegisterProfessionalAsync("Al", "Ert", tag);
        await host.AddProfessionAsync(pro, extra: new() { ["city"] = tag, ["costPerHour"] = 40 });
        var (client, _, _) = await host.RegisterAsync();

        var (status, alert) = await client.PostAsync("/api/alerts", new { name = "Cheap lawyers", searchCriteria = new { city = tag, maxCost = 60, page = 5, pageSize = 50 }, frequency = "weekly" });
        Assert.Equal(HttpStatusCode.Created, status);
        Assert.Equal("weekly", ApiHost.Str(alert, "frequency"));
        Assert.False(ApiHost.Has(alert.GetProperty("searchCriteria"), "page"), "paging must not be stored in a saved search");
        var id = alert.GetProperty("id").GetInt64();

        Assert.Equal(1, (await client.GetAsync("/api/alerts")).Body.GetArrayLength());
        var (rs, run) = await client.GetAsync($"/api/alerts/{id}/run");
        Assert.Equal(HttpStatusCode.OK, rs);
        Assert.Equal(1, run.GetProperty("total").GetInt32());

        var (pro2, _, _) = await host.RegisterProfessionalAsync("Al2", "Ert", tag);   // a new match appears later: the same alert finds it
        await host.AddProfessionAsync(pro2, extra: new() { ["city"] = tag, ["costPerHour"] = 10 });
        Assert.Equal(2, (await client.GetAsync($"/api/alerts/{id}/run")).Body.GetProperty("total").GetInt32());
    }

    [Fact]
    public async Task Alerts_belong_to_their_owner_alone()
    {
        await using var host = await ApiHost.StartAsync();
        var (owner, _, _) = await host.RegisterAsync();
        var (other, _, _) = await host.RegisterAsync();
        var id = (await owner.PostAsync("/api/alerts", new { name = "mine", searchCriteria = new { city = "Beirut" } })).Body.GetProperty("id").GetInt64();
        Assert.Equal(0, (await other.GetAsync("/api/alerts")).Body.GetArrayLength());
        var (s1, b1) = await other.DeleteAsync($"/api/alerts/{id}");
        Assert.Equal(HttpStatusCode.NotFound, s1);
        Assert.Equal("ALERT_NOT_FOUND", TestClient.Error(b1));
        Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync($"/api/alerts/{id}/run")).Status);
        Assert.Equal(1, (await owner.GetAsync("/api/alerts")).Body.GetArrayLength());   // survived the attempted delete
        Assert.Equal(HttpStatusCode.NoContent, (await owner.DeleteAsync($"/api/alerts/{id}")).Status);
        Assert.Equal(0, (await owner.GetAsync("/api/alerts")).Body.GetArrayLength());
    }

    [Fact]
    public async Task Alert_input_is_validated_and_each_user_is_capped_at_twenty()
    {
        await using var host = await ApiHost.StartAsync();
        var (c, _, _) = await host.RegisterAsync();
        Assert.Equal(HttpStatusCode.BadRequest, (await c.PostAsync("/api/alerts", new { name = "", searchCriteria = new { } })).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await c.PostAsync("/api/alerts", new { name = "x" })).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await c.PostAsync("/api/alerts", new { name = "x", searchCriteria = new { minAge = 50, maxAge = 10 } })).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await c.PostAsync("/api/alerts", new { name = "x", searchCriteria = new { }, frequency = "hourly" })).Status);
        for (var i = 0; i < 20; i++) Assert.Equal(HttpStatusCode.Created, (await c.PostAsync("/api/alerts", new { name = $"a{i}", searchCriteria = new { } })).Status);
        var (status, body) = await c.PostAsync("/api/alerts", new { name = "one too many", searchCriteria = new { } });
        Assert.Equal(HttpStatusCode.Conflict, status);
        Assert.Equal("ALERT_LIMIT", TestClient.Error(body));
    }
}
