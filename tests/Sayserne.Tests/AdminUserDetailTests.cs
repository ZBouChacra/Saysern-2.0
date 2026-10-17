using System.Net;
using System.Text.Json;

namespace Sayserne.Tests;

public class AdminUserDetailTests
{
    [Fact]
    public async Task Staff_can_open_everything_about_one_person_and_each_opening_is_audited_and_never_includes_secrets()
    {
        await using var iso = await IsolatedHost.StartAsync();
        var (admin, _, _) = await iso.Host.CreateAdminAsync();
        var (pro, proEmail, proId) = await iso.Host.RegisterProfessionalAsync("Dina", "Detail", "Detailtown", "LB");
        var listing = await iso.Host.AddProfessionAsync(pro, extra: new() { ["city"] = "Detailtown", ["country"] = "LB" });
        var (client, _, clientId) = await iso.Host.RegisterAsync();
        Assert.Equal(HttpStatusCode.Created, (await client.PostAsync("/api/appointments", new { professionalId = proId, professionId = listing, startsAt = ApiHost.FutureSlot(), durationMinutes = 60 })).Status);
        await pro.PutAsync("/api/me/availability", new { slots = new[] { new { dayOfWeek = 2, startTime = "09:00", endTime = "12:00" } } });   // (hours are set after booking: booking outside them would now be refused)

        Assert.Equal(HttpStatusCode.Unauthorized, (await iso.Host.NewClient().GetAsync($"/api/admin/users/{proId}")).Status);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync($"/api/admin/users/{proId}")).Status);                  // an ordinary account is not staff
        var (blind, _, _) = await iso.Host.CreateStaffAsync("settings.read");
        Assert.Equal(HttpStatusCode.Forbidden, (await blind.GetAsync($"/api/admin/users/{proId}")).Status);                      // staff need users.read
        var (reader, _, _) = await iso.Host.CreateStaffAsync("users.read", "audit.read");
        Assert.Equal(HttpStatusCode.NotFound, (await reader.GetAsync("/api/admin/users/99999999")).Status);

        var (st, body) = await reader.GetAsync($"/api/admin/users/{proId}");
        Assert.Equal(HttpStatusCode.OK, st);
        Assert.Equal((proEmail.ToLowerInvariant(), "professional"), (body.GetProperty("user").GetProperty("email").GetString(), body.GetProperty("user").GetProperty("profileType").GetString()));
        Assert.Equal(proEmail.ToLowerInvariant(), body.GetProperty("profile").GetProperty("email").GetString());                   // the private profile: staff see what the public cannot
        Assert.Equal(1, body.GetProperty("professions").GetArrayLength());
        Assert.Equal("Detailtown", body.GetProperty("professions")[0].GetProperty("city").GetString());
        var slot = body.GetProperty("availability").EnumerateArray().Single();
        Assert.Equal((2, "09:00"), (slot.GetProperty("dayOfWeek").GetInt32(), slot.GetProperty("startTime").GetString()));
        Assert.Equal(1, body.GetProperty("appointments").GetArrayLength());
        var counts = body.GetProperty("counts");
        Assert.Equal((0L, 1L, 0L), (counts.GetProperty("appointmentsAsClient").GetInt64(), counts.GetProperty("appointmentsAsProfessional").GetInt64(), counts.GetProperty("reviewsReceived").GetInt64()));
        var clientCounts = (await reader.GetAsync($"/api/admin/users/{clientId}")).Body.GetProperty("counts");
        Assert.Equal((1L, 0L), (clientCounts.GetProperty("appointmentsAsClient").GetInt64(), clientCounts.GetProperty("appointmentsAsProfessional").GetInt64()));
        var text = body.GetRawText().ToLowerInvariant();
        Assert.False(text.Contains("passwordhash") || text.Contains("password_hash") || text.Contains("totp"));                     // never a secret

        var audit = (await reader.GetAsync("/api/admin/audit?action=users.view&pageSize=10")).Body;
        Assert.Equal(2, audit.GetProperty("total").GetInt32());                                                                   // both openings, with who opened what
        Assert.Contains(proEmail.ToLowerInvariant(), audit.GetProperty("items")[1].GetProperty("summary").GetString()!.ToLowerInvariant());
    }
}
