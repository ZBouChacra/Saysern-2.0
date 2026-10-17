using System.Net;
using System.Text.Json;

namespace Sayserne.Tests;

public class ContactTests
{
    private static object Form(string? name = "Mona", string? email = "mona@example.test", string? subject = "A question", string? message = "How do I list my services?", string? topic = null, string? language = null, string? website = null) =>
        new { name, email, subject, message, topic, language, website };

    [Fact]
    public async Task The_public_form_validates_stores_the_message_and_attaches_the_account_of_a_signed_in_sender()
    {
        await using var site = await MailSite.StartAsync(emailOn: false);
        await site.Set("Contact.MessagesPerHour", "200");                                                                   // (refused attempts count towards the hourly limit too)
        var anon = site.Host.NewClient();
        async Task<(HttpStatusCode, JsonElement)> Send(object body, TestClient? who = null) => await (who ?? anon).PostAsync("/api/contact", body);
        foreach (var (field, body) in new (string, object)[]
        {
            ("name", Form(name: "")), ("name", Form(name: new string('n', 101))), ("email", Form(email: "not-an-address")), ("email", Form(email: "")),
            ("subject", Form(subject: "  ")), ("subject", Form(subject: new string('s', 151))), ("message", Form(message: "")), ("message", Form(message: new string('m', 4001))),
        })
        {
            var (st, b) = await Send(body);
            Assert.True(st == HttpStatusCode.BadRequest && b.GetProperty("errors").TryGetProperty(field, out _), $"{field}: {st} {b}");
        }
        Assert.Equal("0", await site.Iso.Db.ScalarAsync("SELECT COUNT(*) FROM contact_messages"));
        var (ok, answer) = await Send(Form(topic: "support", language: "ar"));
        Assert.Equal(HttpStatusCode.Accepted, ok);
        Assert.True(answer.GetProperty("received").GetBoolean());
        var row = await site.Iso.Db.ScalarAsync("SELECT CONCAT_WS('|', status, topic, language, IFNULL(user_id, 'anon'), email) FROM contact_messages");
        Assert.Equal("new|support|ar|anon|mona@example.test", row);
        var (user, _, id) = await site.Host.RegisterAsync();
        await Send(Form(topic: "no-such-topic", language: "xx"), user);                                                    // an unknown topic is dropped, an unknown language falls back to the default
        Assert.Equal($"|en|{id}", await site.Iso.Db.ScalarAsync($"SELECT CONCAT(IFNULL(topic, ''), '|', language, '|', user_id) FROM contact_messages WHERE user_id = {id}"));
        await site.Set("Contact.MaxMessageLength", "100");
        Assert.Equal(HttpStatusCode.BadRequest, (await Send(Form(message: new string('m', 101)))).Item1);                 // the limit is a setting
        Assert.Equal(100, (await anon.GetAsync("/api/config/public")).Body.GetProperty("Contact.MaxMessageLength").GetInt32());
    }

    [Fact]
    public async Task A_filled_in_trap_field_is_kept_as_spam_without_telling_the_bot_or_the_staff()
    {
        await using var site = await MailSite.StartAsync();
        await site.Set("Contact.NotifyAddress", "staff@sayserne.test");
        var (st, b) = await site.Host.NewClient().PostAsync("/api/contact", Form(website: "http://spam.example"));
        Assert.Equal((HttpStatusCode.Accepted, true), (st, b.GetProperty("received").GetBoolean()));                    // the bot sees the same answer as everyone
        Assert.Equal("spam", await site.Iso.Db.ScalarAsync("SELECT status FROM contact_messages"));
        Assert.Empty(await site.Mail());                                                                                   // and nobody is e-mailed about it
    }

    [Fact]
    public async Task Each_address_may_send_only_a_few_messages_an_hour()
    {
        await using var site = await MailSite.StartAsync(emailOn: false);
        await site.Set("Contact.MessagesPerHour", "3");
        var c = site.Host.NewClient();
        var codes = new List<HttpStatusCode>();
        for (var i = 0; i < 5; i++) codes.Add((await c.PostAsync("/api/contact", Form())).Item1);
        Assert.Equal("202,202,202,429,429", string.Join(",", codes.Select(c => (int)c)));
        Assert.Equal("3", await site.Iso.Db.ScalarAsync("SELECT COUNT(*) FROM contact_messages"));
        for (var i = 0; i < 40; i++) Assert.Equal(HttpStatusCode.OK, (await c.GetAsync("/api/lookups/contact_topic")).Status);   // browsing is never limited
    }

    [Fact]
    public async Task Staff_can_be_told_by_e_mail_when_a_message_arrives()
    {
        await using var site = await MailSite.StartAsync();
        await site.Host.NewClient().PostAsync("/api/contact", Form());
        Assert.Empty(await site.Mail());                                                                                   // no address set: no e-mail
        await site.Set("Contact.NotifyAddress", "staff@sayserne.test");
        await site.Host.NewClient().PostAsync("/api/contact", Form(subject: "Please call me", message: "My number is 71 000 000"));
        var mail = Assert.Single(await site.Mail());
        Assert.Equal("staff@sayserne.test", mail.To);
        Assert.Equal("New contact message: Please call me", mail.Subject);
        Assert.Contains("My number is 71 000 000", mail.Text); Assert.Contains("mona@example.test", mail.Text);
    }

    [Fact]
    public async Task Only_staff_with_the_right_permission_can_read_or_answer_and_the_inbox_filters_and_searches()
    {
        await using var site = await MailSite.StartAsync(emailOn: false);
        var c = site.Host.NewClient();
        await c.PostAsync("/api/contact", Form("Alice", "alice@example.test", "Booking trouble", "I cannot book an appointment"));
        await c.PostAsync("/api/contact", Form("Bob", "bob@example.test", "Advertising rates", "What does a banner cost?"));
        await c.PostAsync("/api/contact", Form("Spammer", "spam@example.test", "Cheap pills", "buy now", website: "x"));
        Assert.Equal(HttpStatusCode.Unauthorized, (await c.GetAsync("/api/admin/contact")).Status);
        var (nobody, _, _) = await site.Host.CreateStaffAsync("users.read");
        Assert.Equal(HttpStatusCode.Forbidden, (await nobody.GetAsync("/api/admin/contact")).Status);
        var (reader, _, _) = await site.Host.CreateStaffAsync("contact.read");
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.PostAsync("/api/admin/contact/1/reply", new { body = "x" })).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.PostAsync("/api/admin/contact/1/status", new { status = "archived" })).Status);

        async Task<string[]> Names(string q) => (await reader.GetAsync("/api/admin/contact?pageSize=50" + q)).Body.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("name").GetString()!).ToArray();
        Assert.Equal("Bob,Alice,Spammer", string.Join(",", await Names("")));                                              // unanswered first, newest first; spam last
        Assert.Equal(new[] { "Spammer" }, await Names("&status=spam"));
        Assert.Equal(new[] { "Bob", "Alice" }, await Names("&status=new"));                                                // newest first
        Assert.Equal(new[] { "Alice" }, await Names("&search=book"));                                                      // by text of the message
        Assert.Equal(new[] { "Bob" }, await Names("&search=bob%40example"));                                               // by address
        Assert.Equal(new[] { "Bob" }, await Names("&search=Advertising"));                                                 // by subject
        Assert.Empty(await Names("&search=%25"));                                                                          // a wildcard is just text
        var one = (await reader.GetAsync("/api/admin/contact/1")).Body;
        Assert.Equal(("Alice", "I cannot book an appointment"), (one.GetProperty("name").GetString(), one.GetProperty("body").GetString()));
        Assert.Equal(HttpStatusCode.NotFound, (await reader.GetAsync("/api/admin/contact/99999")).Status);
        Assert.Equal(1, (await reader.GetAsync("/api/admin/contact?status=new&pageSize=1&page=2")).Body.GetProperty("items").GetArrayLength());   // paging
    }

    [Fact]
    public async Task A_reply_is_emailed_to_the_address_the_person_typed_in_their_language_and_then_recorded()
    {
        await using var site = await MailSite.StartAsync();
        var c = site.Host.NewClient();
        await c.PostAsync("/api/contact", Form("Rima", "rima@example.test", "Ma sujet", "كيف أضيف خدماتي؟", language: "ar"));
        await c.PostAsync("/api/contact", Form("Tom", "tom@example.test", "Pricing", "How much?", language: "en"));
        var (manager, _, mid) = await site.Host.CreateStaffAsync("contact.manage", "contact.read");
        Assert.Equal("REPLY_REQUIRED", TestClient.Error((await manager.PostAsync("/api/admin/contact/1/reply", new { body = "  " })).Body));
        Assert.Equal(HttpStatusCode.NotFound, (await manager.PostAsync("/api/admin/contact/99999/reply", new { body = "hello" })).Status);

        var (st, replied) = await manager.PostAsync("/api/admin/contact/1/reply", new { body = "أضف خدماتك من صفحة ملفك الشخصي." });
        Assert.Equal(HttpStatusCode.OK, st);
        Assert.Equal(("replied", "أضف خدماتك من صفحة ملفك الشخصي."), (replied.GetProperty("status").GetString(), replied.GetProperty("replyBody").GetString()));
        Assert.NotNull(replied.GetProperty("repliedAt").GetString());
        var arabic = Assert.Single(await site.Mail(m => m.To == "rima@example.test"));
        Assert.Equal("رد: Ma sujet", arabic.Subject);                                                                     // the template of the sender's language
        Assert.Contains("أضف خدماتك", arabic.Text); Assert.Contains("كيف أضيف خدماتي؟", arabic.Text);                     // the answer and the original message
        Assert.Contains("dir=\"rtl\"", arabic.Html);
        await manager.PostAsync("/api/admin/contact/2/reply", new { body = "About 10 USD a day." });
        var english = Assert.Single(await site.Mail(m => m.To == "tom@example.test"));
        Assert.Equal("Re: Pricing", english.Subject); Assert.Contains("About 10 USD a day.", english.Text);
        var audit = (await site.Admin.GetAsync("/api/admin/audit?action=contact.reply")).Body;
        Assert.Equal(2, audit.GetProperty("total").GetInt32());
        _ = mid;
    }

    [Fact]
    public async Task Replying_needs_e_mail_to_be_switched_on_and_a_message_can_be_moved_between_statuses_with_a_note()
    {
        await using var site = await MailSite.StartAsync(emailOn: false);
        await site.Host.NewClient().PostAsync("/api/contact", Form());
        var (manager, _, _) = await site.Host.CreateStaffAsync("contact.manage", "contact.read");
        var (st, b) = await manager.PostAsync("/api/admin/contact/1/reply", new { body = "Hello" });
        Assert.Equal((HttpStatusCode.Conflict, "EMAIL_NOT_CONFIGURED"), (st, TestClient.Error(b)));                       // no e-mail, so no pretending that a reply was sent
        Assert.Equal("new", await site.Iso.Db.ScalarAsync("SELECT status FROM contact_messages"));
        foreach (var status in new[] { "in_progress", "archived", "new", "spam" })
        {
            var (s2, row) = await manager.PostAsync("/api/admin/contact/1/status", new { status, note = "checked " + status });
            Assert.Equal(HttpStatusCode.OK, s2);
            Assert.Equal((status, "checked " + status), (row.GetProperty("status").GetString(), row.GetProperty("internalNote").GetString()));
        }
        Assert.Equal("INVALID_STATUS", TestClient.Error((await manager.PostAsync("/api/admin/contact/1/status", new { status = "deleted" })).Body));
        Assert.Equal(HttpStatusCode.NotFound, (await manager.PostAsync("/api/admin/contact/99999/status", new { status = "archived" })).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await manager.PostAsync("/api/admin/contact/1/status", new { status = "new", note = new string('x', 1001) })).Status);
        Assert.Equal(4, (await site.Admin.GetAsync("/api/admin/audit?action=contact.status")).Body.GetProperty("total").GetInt32());
    }
}
