"""End-to-end journey 08_moderation: drives the real public site (http://127.0.0.1:5173) and back office (http://127.0.0.1:5174) in a browser, against a running API and an empty database.
See tests/e2e/README.md for how to start everything. Environment: SAYSERNE_MIGRATOR_PROJECT (path to api/Sayserne.Migrator), SAYSERNE_MIGRATOR_CONNECTION (to promote the first admin),
E2E_MYSQL (default "mysql -uroot") and E2E_DB (default "sayserne_dev") for the few steps that move a date back in time."""
import tempfile
import asyncio, base64, hashlib, hmac, json, os, struct, subprocess, time, uuid
from playwright.async_api import async_playwright

PUB, ADM = "http://127.0.0.1:5173", "http://127.0.0.1:5174"
PW = "correct-horse-battery-9"
results = []
def check(name, cond, extra=""):
    results.append(bool(cond)); print(("PASS " if cond else "FAIL ") + name + (f"   [{extra}]" if (extra and not cond) else ""))
def totp(secret, step):
    key = base64.b32decode(secret + "=" * ((8 - len(secret) % 8) % 8)); h = hmac.new(key, struct.pack(">Q", step), hashlib.sha1).digest(); o = h[-1] & 15
    return f"{(struct.unpack('>I', h[o:o+4])[0] & 0x7fffffff) % 1000000:06d}"
def mysql(sql): subprocess.run(os.environ.get("E2E_MYSQL", "mysql -uroot").split() + [os.environ.get("E2E_DB", "sayserne_dev"), "-e", sql], check=True, capture_output=True)

async def main():
    uid = uuid.uuid4().hex[:6]; boss = f"boss{uid}@example.test"; last = f"Mod{uid}"
    emails = {n: f"{n.lower()}{uid}@example.test" for n in ("Pia", "Cara", "Dan")}
    async with async_playwright() as p:
        b = await p.chromium.launch(); errors = []
        ctx = await b.new_context(); pg = await ctx.new_page(); await pg.goto(PUB + "/register"); await pg.wait_for_selector("text=Create your account")
        await pg.fill('input[autocomplete="given-name"]', "Boss"); await pg.fill('input[autocomplete="family-name"]', "Tester"); await pg.fill('input[autocomplete="email"]', boss)
        await pg.fill('input[autocomplete="new-password"]', PW); await pg.click("form button.btn"); await pg.wait_for_selector("text=Welcome, Boss"); await ctx.close()
        subprocess.run(["dotnet","run","--no-build","--project",os.environ.get("SAYSERNE_MIGRATOR_PROJECT", "api/Sayserne.Migrator"),"--","promote-admin",boss], env=os.environ, check=True, capture_output=True)

        async def api_user(name, professional=False):
            a = await p.request.new_context(base_url=PUB, extra_http_headers={"Origin": PUB, "X-Requested-With": "sayserne-web"})
            r = await a.post("/api/auth/register", data={"email": emails[name], "password": PW, "firstName": name, "lastName": last}); assert r.status == 201, await r.text()
            if professional:
                assert (await a.put("/api/me/profile", data={"firstName": name, "lastName": last, "profileType": "professional", "country": "LB", "city": "Beirut"})).status == 200
                svc = (await (await a.get("/api/catalog/services")).json())[0]
                assert (await a.post("/api/me/professions", data={"categoryId": svc["categoryId"], "serviceId": svc["id"], "city": "Beirut", "country": "LB", "costPerHour": 50})).status == 201
            return a, (await (await a.get("/api/auth/me")).json())["user"]["id"]
        pia_api, pia_id = await api_user("Pia", True); cara_api, cara_id = await api_user("Cara"); dan_api, dan_id = await api_user("Dan")

        # a completed appointment and a harsh review (through the API: the booking screens are covered elsewhere)
        start = time.strftime("%Y-%m-%dT10:00:00Z", time.gmtime(time.time() + 86400 * 9))
        appt = await (await cara_api.post("/api/appointments", data={"professionalId": pia_id, "startsAt": start, "durationMinutes": 60})).json()
        assert (await pia_api.post(f"/api/appointments/{appt['id']}/status", data={"status": "approved"})).status == 200
        mysql(f"UPDATE appointments SET starts_at = DATE_SUB(UTC_TIMESTAMP(3), INTERVAL 3 DAY) WHERE id = {appt['id']}")
        assert (await pia_api.post(f"/api/appointments/{appt['id']}/status", data={"status": "completed"})).status == 200
        assert (await cara_api.post("/api/reviews", data={"appointmentId": appt["id"], "rating": 1, "comment": "Rude and late, never again"})).status == 201

        # ---- the professional answers the review on the site
        pctx = await b.new_context(viewport={"width": 1200, "height": 950}); pu = await pctx.new_page(); pu.on("pageerror", lambda e: errors.append("pia: " + str(e)))
        pu.on("dialog", lambda d: asyncio.ensure_future(d.accept()))
        await pu.goto(PUB + "/login"); await pu.fill('input[type="email"]', emails["Pia"]); await pu.fill('input[type="password"]', PW); await pu.click("form button.btn"); await pu.wait_for_selector("text=Welcome, Pia")
        await pu.goto(f"{PUB}/professional/{pia_id}"); await pu.wait_for_selector("text=Rude and late")
        await pu.click("button:text-is('Reply')"); await pu.fill('textarea[aria-label="Your reply"]', "I am sorry you felt that way."); await pu.click("button:has-text('Save reply')")
        await pu.wait_for_selector(".reply-box"); check("the professional can answer a review on the site", "I am sorry you felt that way." in await pu.inner_text(".reply-box"))
        await pu.click("button:text-is('Edit your reply')"); await pu.fill('textarea[aria-label="Your reply"]', "I apologise; I will do better."); await pu.click("button:has-text('Save reply')")
        await pu.wait_for_function("document.querySelector('.reply-box')?.textContent.includes('I apologise')"); check("the answer can be edited and is marked as edited", "(edited)" in await pu.inner_text(".reply-box"))
        vctx = await b.new_context(); vis = await vctx.new_page(); await vis.goto(f"{PUB}/professional/{pia_id}"); await vis.wait_for_selector(".reply-box")
        check("a visitor sees the answer under the review but has no reply button", "I apologise" in await vis.inner_text(".reply-box") and await vis.locator("button:text-is('Reply')").count() == 0)

        # ---- the admin hides the review
        actx = await b.new_context(viewport={"width": 1350, "height": 1000}); adm = await actx.new_page(); adm.on("pageerror", lambda e: errors.append("admin: " + str(e)))
        await adm.goto(ADM + "/"); await adm.fill('input[type="email"]', boss); await adm.fill('input[type="password"]', PW); await adm.click("button:has-text('Continue')")
        await adm.wait_for_selector("[data-testid=totp-secret]"); secret = await adm.inner_text("[data-testid=totp-secret]")
        await adm.fill('input[autocomplete="one-time-code"]', totp(secret, int(time.time() // 30))); await adm.click("button:has-text('Confirm and continue')"); await adm.wait_for_selector("[data-testid=recovery-codes]"); await adm.click("button:has-text('I have saved them')")
        await adm.wait_for_selector("text=Welcome, Boss")
        await adm.click("aside >> text=Reviews"); await adm.fill('input[aria-label="Search reviews"]', last); await adm.wait_for_selector("tr:has-text('Rude and late')")
        check("staff see the review with the professional's answer", "I apologise" in await adm.locator("tr:has-text('Rude and late')").inner_text())
        await adm.click("button:has-text('Hide…')"); check("hiding needs a reason (the button stays disabled until one is written)", await adm.locator("button:text-is('Hide')").is_disabled())
        await adm.fill('input[aria-label="Reason for hiding"]', "Personal attack"); await adm.click("button:text-is('Hide')"); await adm.wait_for_selector("tr:has-text('Rude and late') >> .pill.bad:has-text('hidden')")
        await vis.reload(); await vis.wait_for_selector("h2:has-text('Reviews')"); await vis.wait_for_timeout(600)
        check("a hidden review disappears from the public page", "Rude and late" not in await vis.inner_text("main") and "No reviews yet" in await vis.inner_text("main"))
        await adm.select_option('select[aria-label="Review status"]', "hidden"); await adm.wait_for_selector("tr:has-text('Personal attack')"); check("staff still see it, with the reason", True)
        await adm.click("button:has-text('Restore')"); await adm.wait_for_function("document.querySelectorAll('tbody tr').length === 0 || !document.body.textContent.includes('Personal attack')")
        await vis.reload(); await vis.wait_for_selector("text=Rude and late"); check("restoring brings it back", True)

        # ---- two people chat; one deletes a message and then the conversation
        async def login(name):
            c = await b.new_context(viewport={"width": 1200, "height": 900}); pg = await c.new_page(); pg.on("pageerror", lambda e: errors.append(name + ": " + str(e))); pg.on("dialog", lambda d: asyncio.ensure_future(d.accept()))
            await pg.goto(PUB + "/login"); await pg.fill('input[type="email"]', emails[name]); await pg.fill('input[type="password"]', PW); await pg.click("form button.btn"); await pg.wait_for_selector(f"text=Welcome, {name}"); return pg
        cara = await login("Cara"); dan = await login("Dan")
        room = (await (await cara_api.post("/api/chat/rooms", data={"userId": dan_id})).json())["id"]
        await cara.goto(f"{PUB}/chats/{room}"); await cara.wait_for_selector('input[aria-label="Message"]')
        for text in ("hello Dan", "this message will be deleted"):
            await cara.fill('input[aria-label="Message"]', text); await cara.click("button:has-text('Send')"); await cara.wait_for_selector(f".bubble:has-text('{text}')")
        await dan.goto(f"{PUB}/chats/{room}"); await dan.wait_for_selector(".bubble:has-text('this message will be deleted')")
        await dan.fill('input[aria-label="Message"]', "hi Cara"); await dan.click("button:has-text('Send')"); await dan.wait_for_selector(".bubble:has-text('hi Cara')")
        await cara.locator(".bubble:has-text('this message will be deleted') >> button[aria-label='Delete message']").click()
        await cara.wait_for_selector(".bubble.deleted"); check("a person can delete their own message", "this message was deleted" in (await cara.inner_text(".bubble.deleted")).lower())
        check("they cannot delete the other person's message", await cara.locator(".bubble:has-text('hi Cara') >> button[aria-label='Delete message']").count() == 0)
        await dan.wait_for_selector(".bubble.deleted", timeout=15000); check("the other person sees that it was deleted, not what it said", "this message will be deleted" not in await dan.inner_text(".thread"))
        await cara.click("button:has-text('Delete conversation')"); await cara.wait_for_selector("text=No conversations yet")
        check("deleting the conversation empties the list for that person only", await dan.locator(".bubble:has-text('hello Dan')").count() == 1)

        # ---- staff read it (audited) and write in it
        await adm.click("aside >> text=Conversations"); await adm.select_option('select[aria-label="Conversation filter"]', "deleted"); await adm.fill('input[aria-label="Search conversations"]', f"Cara {last}")
        await adm.wait_for_selector("tr:has-text('chat deleted by Cara')"); row = await adm.locator("tr:has-text('chat deleted by Cara')").inner_text()
        check("staff see which conversations had deletions and who deleted the chat", "1 message" in row and "Cara" in row, row)
        await adm.locator("tr:has-text('chat deleted by Cara') >> button:has-text('Open')").click(); await adm.wait_for_selector("[data-testid=chat-thread]")
        thread = await adm.inner_text("[data-testid=chat-thread]"); check("staff can read the deleted message and see who deleted it", "this message will be deleted" in thread and "deleted by Cara" in thread, thread)
        audit = await (await adm.context.request.get(ADM + "/api/admin/audit?action=chats.read", headers={"X-Requested-With": "sayserne-admin"})).json()
        check("opening the conversation was recorded in the audit log", audit["total"] >= 1)
        await adm.fill("[data-testid=chat-thread] textarea", "Please keep the conversation polite."); await adm.click("button:has-text('Send as the team')")
        await dan.wait_for_selector(".bubble.staff", timeout=15000); check("both people see the team's message highlighted", "saysern" in (await dan.inner_text(".bubble.staff")).lower() and "polite" in await dan.inner_text(".bubble.staff"))
        await cara.goto(PUB + "/chats"); await cara.wait_for_selector("button.room", timeout=15000); check("a team message brings the conversation back for the person who deleted it", await cara.locator("button.room").count() == 1)
        print("   page errors:", errors); check("no uncaught browser errors", not errors)
        await b.close()
asyncio.run(main())
print(f"\n{sum(results)}/{len(results)} moderation end-to-end checks passed")
