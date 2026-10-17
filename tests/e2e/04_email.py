"""End-to-end journey 04_email: drives the real public site (http://127.0.0.1:5173) and back office (http://127.0.0.1:5174) in a browser, against a running API and an empty database.
See tests/e2e/README.md for how to start everything. Environment: SAYSERNE_MIGRATOR_PROJECT (path to api/Sayserne.Migrator), SAYSERNE_MIGRATOR_CONNECTION (to promote the first admin),
E2E_MYSQL (default "mysql -uroot") and E2E_DB (default "sayserne_dev") for the few steps that move a date back in time."""
import tempfile
import os
import asyncio, base64, email as pyemail, hashlib, hmac, os, re, struct, subprocess, time, uuid
from email import policy
from playwright.async_api import async_playwright, expect

PUB, ADM = "http://127.0.0.1:5173", "http://127.0.0.1:5174"
PW = "correct-horse-battery-9"
results = []; inbox = []
def check(name, cond, extra=""):
    results.append(bool(cond)); print(("PASS " if cond else "FAIL ") + name + (f"   [{extra}]" if (extra and not cond) else ""))
def totp(secret, step):
    key = base64.b32decode(secret + "=" * ((8 - len(secret) % 8) % 8)); h = hmac.new(key, struct.pack(">Q", step), hashlib.sha1).digest(); o = h[-1] & 15
    return f"{(struct.unpack('>I', h[o:o+4])[0] & 0x7fffffff) % 1000000:06d}"

async def smtp_client(reader, writer):          # a minimal SMTP server: it keeps every message it is given
    writer.write(b"220 sink ready\r\n"); await writer.drain(); rcpt = []
    while True:
        line = await reader.readline()
        if not line: break
        cmd = line.decode(errors="replace").strip(); up = cmd.upper()
        if up.startswith(("EHLO", "HELO")): writer.write(b"250-sink\r\n250 8BITMIME\r\n")
        elif up.startswith("MAIL"): rcpt = []; writer.write(b"250 ok\r\n")
        elif up.startswith("RCPT"): rcpt.append(re.search(r"<(.*)>", cmd).group(1)); writer.write(b"250 ok\r\n")
        elif up == "DATA":
            writer.write(b"354 go\r\n"); await writer.drain(); data = b""
            while True:
                l = await reader.readline()
                if l in (b".\r\n", b""): break
                data += l[1:] if l.startswith(b"..") else l
            m = pyemail.message_from_bytes(data, policy=policy.default); body = m.get_body(preferencelist=("plain",))
            inbox.append({"to": rcpt[0] if rcpt else "", "subject": str(m["subject"]), "text": body.get_content() if body else ""}); writer.write(b"250 queued\r\n")
        elif up == "QUIT": writer.write(b"221 bye\r\n"); await writer.drain(); break
        else: writer.write(b"250 ok\r\n")
        await writer.drain()
    writer.close()

async def wait_mail(pred, timeout=25):
    end = time.time() + timeout
    while time.time() < end:
        for m in inbox:
            if pred(m): return m
        await asyncio.sleep(0.4)
    return None

async def main():
    uid = uuid.uuid4().hex[:6]
    boss, cara, pia = f"boss{uid}@example.test", f"cara{uid}@example.test", f"pia{uid}@example.test"
    server = await asyncio.start_server(smtp_client, "127.0.0.1", 2525)
    async with async_playwright() as p:
        b = await p.chromium.launch(); errors = []
        # ---- an admin, created while email is still off
        ctx = await b.new_context(); pg = await ctx.new_page(); await pg.goto(PUB + "/register"); await pg.wait_for_selector("text=Create your account")
        await pg.fill('input[autocomplete="given-name"]', "Boss"); await pg.fill('input[autocomplete="family-name"]', "Tester"); await pg.fill('input[autocomplete="email"]', boss)
        await pg.fill('input[autocomplete="new-password"]', PW); await pg.click("form button.btn"); await pg.wait_for_selector("text=Welcome, Boss"); await ctx.close()
        subprocess.run(["dotnet","run","--no-build","--project",os.environ.get("SAYSERNE_MIGRATOR_PROJECT", "api/Sayserne.Migrator"),"--","promote-admin",boss], env=os.environ, check=True, capture_output=True)
        actx = await b.new_context(viewport={"width": 1250, "height": 950}); adm = await actx.new_page(); adm.on("pageerror", lambda e: errors.append("admin: " + str(e)))
        await adm.goto(ADM + "/"); await adm.fill('input[type="email"]', boss); await adm.fill('input[type="password"]', PW); await adm.click("button:has-text('Continue')")
        await adm.wait_for_selector("[data-testid=totp-secret]"); secret = await adm.inner_text("[data-testid=totp-secret]")
        await adm.fill('input[autocomplete="one-time-code"]', totp(secret, int(time.time() // 30))); await adm.click("button:has-text('Confirm and continue')"); await adm.wait_for_selector("[data-testid=recovery-codes]"); await adm.click("button:has-text('I have saved them')")
        await adm.wait_for_selector("text=Welcome, Boss"); await adm.click("aside >> text=Settings"); await adm.wait_for_selector('[data-setting="Email.Provider"]')
        await adm.fill('input[aria-label="Send test email to"]', "owner@example.test"); await adm.click("button:has-text('Send test email')")
        await adm.wait_for_selector("section[aria-label='Test email'] [role=status]"); off_msg = await adm.inner_text("section[aria-label='Test email'] [role=status]")
        check("while email is off the test box says why it cannot send", "not switched on" in off_msg, off_msg)

        async def setting(key, value):
            row = adm.locator(f'[data-setting="{key}"]'); await row.locator("input").fill(value); await row.locator("button:has-text('Save')").click(); await expect(row.locator("[role=status]")).to_contain_text("Saved")
        for key, value in [("Email.SmtpHost", "127.0.0.1"), ("Email.SmtpPort", "2525"), ("Email.SmtpSecurity", "none"), ("Email.FromAddress", "hello@sayserne.test"), ("Site.PublicUrl", PUB)]: await setting(key, value)
        await adm.locator('[data-setting="Email.SmtpPassword"] input').fill("pw-that-must-stay-secret"); await adm.locator('[data-setting="Email.SmtpPassword"] button:has-text("Save")').click()
        await expect(adm.locator('[data-setting="Email.SmtpPassword"] [role=status]')).to_contain_text("encrypted")
        pw_row = adm.locator('[data-setting="Email.SmtpPassword"]')
        check("the SMTP password is write-only: the field is empty again and says a value is stored", await pw_row.locator("input").input_value() == "" and "stored" in (await pw_row.inner_text()).lower() and "pw-that-must-stay-secret" not in await adm.content())
        await setting("Email.Provider", "smtp")

        await adm.fill('input[aria-label="Send test email to"]', "owner@example.test"); await adm.click("button:has-text('Send test email')")
        await adm.wait_for_selector("section[aria-label='Test email'] [role=status]"); msg = await adm.inner_text("section[aria-label='Test email'] [role=status]")
        t = await wait_mail(lambda m: m["subject"].startswith("Test email"))
        check("the test email reaches the mail server", t is not None and t["to"] == "owner@example.test" and "your email settings work" in t["text"], str(inbox))
        check("the admin is told it was sent", "was sent" in msg, msg)
        await adm.fill('input[aria-label="Send test email to"]', "broken"); await adm.click("button:has-text('Send test email')")
        await adm.wait_for_function("document.querySelector(\"section[aria-label='Test email'] [role=status]\")?.textContent?.includes('valid')"); check("an invalid address is refused with its own message", True)

        # ---- a professional (through the API) and a customer (through the pages)
        api = await p.request.new_context(base_url=PUB, extra_http_headers={"Origin": PUB})
        r = await api.post("/api/auth/register", data={"email": pia, "password": PW, "firstName": "Pia", "lastName": "Pro"}); assert r.status == 201, await r.text()
        await api.put("/api/me/profile", data={"firstName": "Pia", "lastName": "Pro", "profileType": "professional", "country": "LB", "city": "Beirut"})
        svc = (await (await api.get("/api/catalog/services")).json())[0]
        assert (await api.post("/api/me/professions", data={"categoryId": svc["categoryId"], "serviceId": svc["id"], "city": "Beirut", "country": "LB"})).status == 201
        pro_id = (await (await api.get("/api/auth/me")).json())["user"]["id"]
        vm = await wait_mail(lambda m: m["to"] == pia and "Confirm" in m["subject"]); tok = re.search(r"token=([\w-]+)", vm["text"]).group(1)
        assert (await api.post("/api/auth/verify-email", data={"token": tok})).status == 200

        cctx = await b.new_context(viewport={"width": 1150, "height": 900}); cu = await cctx.new_page(); cu.on("pageerror", lambda e: errors.append("cara: " + str(e)))
        await cu.goto(PUB + "/register"); await cu.wait_for_selector("text=Create your account")
        await cu.fill('input[autocomplete="given-name"]', "Cara"); await cu.fill('input[autocomplete="family-name"]', "Tester"); await cu.fill('input[autocomplete="email"]', cara)
        await cu.fill('input[autocomplete="new-password"]', PW); await cu.click("form button.btn"); await cu.wait_for_selector("text=Welcome, Cara")
        await cu.reload(); await cu.wait_for_selector(".notice")
        check("an unconfirmed customer sees the 'please confirm' notice", "confirm your email" in (await cu.inner_text(".notice")).lower())
        status = await cu.evaluate("fetch('/api/appointments',{method:'POST',credentials:'same-origin',headers:{'Content-Type':'application/json','X-Requested-With':'sayserne-web'},body:JSON.stringify({professionalId:%d,startsAt:new Date(Date.now()+864e5*40).toISOString()})}).then(r=>r.status)" % pro_id)
        check("…and cannot book yet (403)", status == 403, str(status))

        m = await wait_mail(lambda m: m["to"] == cara and "Confirm" in m["subject"])
        check("the verification email arrives with a link on the configured address", m is not None and m["text"].count(PUB + "/verify-email?token=") == 1, str(m))
        link = re.search(r"https?://\S+/verify-email\?token=[\w-]+", m["text"]).group(0)
        await cu.goto(link); await cu.wait_for_selector("text=your email address is confirmed")
        check("following the link confirms the address", True)
        await cu.goto(PUB + "/"); await cu.wait_for_timeout(500); check("the notice is gone afterwards", await cu.locator(".notice").count() == 0)
        await cu.goto(link); await cu.wait_for_selector("[role=alert]"); check("the same link cannot be used twice", "invalid or has expired" in (await cu.inner_text("[role=alert]")).lower())

        status = await cu.evaluate("fetch('/api/appointments',{method:'POST',credentials:'same-origin',headers:{'Content-Type':'application/json','X-Requested-With':'sayserne-web'},body:JSON.stringify({professionalId:%d,startsAt:new Date(Date.now()+864e5*40).toISOString()})}).then(r=>r.status)" % pro_id)
        check("a confirmed customer can book", status == 201, str(status))
        bm = await wait_mail(lambda m: m["to"] == pia and "appointment request" in m["subject"].lower())
        check("the professional is emailed about the request, with a link", bm is not None and PUB + "/appointments" in bm["text"], str(inbox[-3:]))

        # ---- forgot password, end to end
        await cu.context.clear_cookies(); await cu.goto(PUB + "/login")
        try:
            await cu.wait_for_selector("text=Forgot your password?", timeout=30000); await cu.click("text=Forgot your password?", timeout=30000); await cu.wait_for_selector('input[type="email"]', timeout=30000); await cu.fill('input[type="email"]', cara); await cu.click("button:has-text('Send the link')", timeout=30000)
        except Exception as ex:
            print("DEBUG url:", cu.url, "| body:", (await cu.inner_text("body"))[:400].replace("\n", " | ")); raise
        await cu.wait_for_selector("[role=status]"); check("the page answers without revealing anything", "if there is an account" in (await cu.inner_text("[role=status]")).lower())
        rm = await wait_mail(lambda m: m["to"] == cara and "Reset" in m["subject"]); rlink = re.search(r"https?://\S+/reset-password\?token=[\w-]+", rm["text"]).group(0)
        await cu.goto(rlink); await cu.fill('input[type="password"]', "short"); await cu.click("button:has-text('Change password')"); await cu.wait_for_selector("[role=alert]")
        check("a weak new password is refused and the link still works", "at least" in (await cu.inner_text("[role=alert]")).lower())
        await cu.fill('input[type="password"]', "My-new-password-42"); await cu.click("button:has-text('Change password')"); await cu.wait_for_selector("text=Password changed")
        await cu.wait_for_selector("main a.btn:has-text('Sign in')"); await cu.click("main a.btn:has-text('Sign in')"); await cu.wait_for_selector('input[type="email"]'); await cu.fill('input[type="email"]', cara); await cu.fill('input[type="password"]', PW); await cu.click("form button.btn"); await cu.wait_for_selector("[role=alert]")
        check("the old password no longer works", True)
        await cu.fill('input[type="password"]', "My-new-password-42"); await cu.click("form button.btn"); await cu.wait_for_selector("text=Welcome, Cara"); check("the new password does", True)

        # ---- notification choices
        pctx = await b.new_context(viewport={"width": 1150, "height": 900}); pu = await pctx.new_page(); pu.on("pageerror", lambda e: errors.append("pia: " + str(e)))
        await pu.goto(PUB + "/login"); await pu.fill('input[type="email"]', pia); await pu.fill('input[type="password"]', PW); await pu.click("form button.btn"); await pu.wait_for_selector("text=Welcome, Pia")
        await pu.goto(PUB + "/settings"); await pu.wait_for_selector("table.t")
        heads = await pu.locator("table.t th").all_inner_texts(); check("only the channels that are switched on are offered", [h.lower() for h in heads] == ["email me when…", "e-mail"], str(heads))
        mandatory = pu.locator('input[aria-label^="Confirm your e-mail address"]'); check("mandatory emails are shown as always on", await mandatory.is_disabled() and await mandatory.is_checked())
        box = pu.locator('input[aria-label="I receive a new message (E-mail)"]'); await box.uncheck(); await pu.wait_for_timeout(600); await pu.reload(); await pu.wait_for_selector("table.t")
        check("a choice is remembered", not await pu.locator('input[aria-label="I receive a new message (E-mail)"]').is_checked())

        # ---- the email log
        await adm.click("aside >> text=Email log"); await adm.wait_for_selector("td code:text('verify_email')")
        page_text = await adm.inner_text("main"); check("the email log lists what was sent, never the content", "verify_email" in page_text and "booking_requested" in page_text and "token=" not in page_text and "sent" in page_text)
        print("   page errors:", errors); check("no uncaught browser errors", not errors)
        await b.close()
    server.close()
asyncio.run(main())
print(f"\n{sum(results)}/{len(results)} email end-to-end checks passed")
