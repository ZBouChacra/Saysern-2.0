"""End-to-end journey 10_phase4: drives the real public site (http://127.0.0.1:5173) and back office (http://127.0.0.1:5174) in a browser, against a running API and an empty database.
See tests/e2e/README.md for how to start everything. Environment: SAYSERNE_MIGRATOR_PROJECT (path to api/Sayserne.Migrator), SAYSERNE_MIGRATOR_CONNECTION (to promote the first admin),
E2E_MYSQL (default "mysql -uroot") and E2E_DB (default "sayserne_dev") for the few steps that move a date back in time."""
import tempfile
import os
import asyncio, base64, email as pyemail, hashlib, hmac, os, re, struct, subprocess, time, uuid
from email import policy
from playwright.async_api import async_playwright

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
    uid = uuid.uuid4().hex[:6]; boss = f"boss{uid}@example.test"; rana = f"rana{uid}@example.test"; NEWPW = "A-new-password-4242"
    server = await asyncio.start_server(smtp_client, "127.0.0.1", 2526)
    async with async_playwright() as p:
        b = await p.chromium.launch(); errors = []
        ctx = await b.new_context(); pg = await ctx.new_page(); await pg.goto(PUB + "/register"); await pg.wait_for_selector("text=Create your account")
        await pg.fill('input[autocomplete="given-name"]', "Boss"); await pg.fill('input[autocomplete="family-name"]', "Tester"); await pg.fill('input[autocomplete="email"]', boss)
        await pg.fill('input[autocomplete="new-password"]', PW); await pg.click("form button.btn"); await pg.wait_for_selector("text=Welcome, Boss"); await ctx.close()
        subprocess.run(["dotnet","run","--no-build","--project",os.environ.get("SAYSERNE_MIGRATOR_PROJECT", "api/Sayserne.Migrator"),"--","promote-admin",boss], env=os.environ, check=True, capture_output=True)
        actx = await b.new_context(viewport={"width": 1350, "height": 1000}); adm = await actx.new_page(); adm.on("pageerror", lambda e: errors.append("admin: " + str(e)))
        await adm.goto(ADM + "/"); await adm.fill('input[type="email"]', boss); await adm.fill('input[type="password"]', PW); await adm.click("button:has-text('Continue')")
        await adm.wait_for_selector("[data-testid=totp-secret]"); secret = await adm.inner_text("[data-testid=totp-secret]")
        await adm.fill('input[autocomplete="one-time-code"]', totp(secret, int(time.time() // 30))); await adm.click("button:has-text('Confirm and continue')"); await adm.wait_for_selector("[data-testid=recovery-codes]"); await adm.click("button:has-text('I have saved them')")
        await adm.wait_for_selector("text=Welcome, Boss")
        H = {"X-Requested-With": "sayserne-admin"}
        for k, v in [("Email.SmtpHost", "127.0.0.1"), ("Email.SmtpPort", "2526"), ("Email.SmtpSecurity", "none"), ("Email.FromAddress", "hello@sayserne.test"), ("Site.PublicUrl", PUB), ("Contact.NotifyAddress", "staff@sayserne.test"), ("Email.Provider", "smtp")]:
            r = await adm.context.request.put(ADM + f"/api/admin/settings/{k}", data={"value": v}, headers=H); assert r.status == 200, (k, await r.text())

        # ---- a visitor sees the site in English, switches to Arabic and the whole page turns right-to-left
        v = await b.new_context(viewport={"width": 1250, "height": 950}); vp = await v.new_page(); vp.on("pageerror", lambda e: errors.append("visitor: " + str(e)))
        await vp.goto(PUB + "/"); await vp.wait_for_selector("h1")
        check("the site starts in English, left-to-right", await vp.evaluate("document.documentElement.lang") == "en" and await vp.evaluate("document.documentElement.dir") == "ltr" and "Find the right professional" in await vp.inner_text("h1"))
        check("the home page has its marketing sections", await vp.locator("section.how li").count() == 3 and await vp.locator("section.why .panel").count() == 3 and await vp.locator("section.cta-pro").count() == 1)
        await vp.select_option(".lang-switch select", "ar"); await vp.wait_for_function("document.documentElement.lang === 'ar'")
        check("choosing Arabic switches the language and the direction", await vp.evaluate("document.documentElement.dir") == "rtl" and "اعثر على المحترف" in await vp.inner_text("h1"))
        brand = await vp.locator("header .brand").bounding_box(); signin = await vp.locator("header nav a >> nth=0").bounding_box()
        check("the layout is mirrored: the brand is on the right, the menu on the left", brand["x"] > signin["x"], f"{brand} {signin}")
        check("the footer, the sections and the buttons are in Arabic", (await vp.inner_text("footer")).strip().startswith("من نحن") and "كيف يعمل" in await vp.inner_text("main"))

        # ---- registering in Arabic saves Arabic as the account's language
        await vp.goto(PUB + "/register"); await vp.wait_for_selector("h1")
        check("the register page is in Arabic", "أنشئ حسابك" in await vp.inner_text("h1"))
        await vp.fill('input[autocomplete="given-name"]', "Rana"); await vp.fill('input[autocomplete="family-name"]', "Tester"); await vp.fill('input[autocomplete="email"]', rana)
        await vp.fill('input[autocomplete="new-password"]', PW); await vp.click("form button.btn"); await vp.wait_for_selector("text=Rana")
        me = await (await vp.context.request.get(PUB + "/api/auth/me")).json()
        check("the account remembers the language used when it was created", me["user"]["preferredLanguage"] == "ar", str(me))

        # ---- signing in elsewhere brings the saved language
        v2 = await b.new_context(viewport={"width": 1250, "height": 950}); sp = await v2.new_page(); sp.on("pageerror", lambda e: errors.append("device 2: " + str(e)))
        await sp.goto(PUB + "/login"); await sp.wait_for_selector("h1"); await sp.fill('input[type="email"]', rana); await sp.fill('input[type="password"]', PW); await sp.click("form button.btn")
        await sp.wait_for_function("document.documentElement.lang === 'ar'", timeout=15000)
        check("on another device, signing in switches the site to the person's language", await sp.evaluate("document.documentElement.dir") == "rtl")

        # ---- Settings: language, time zone, theme
        await sp.goto(PUB + "/settings"); await sp.wait_for_selector("main select")
        await sp.locator("main select").nth(0).select_option("en"); await sp.wait_for_function("document.documentElement.lang === 'en'")
        check("the language can be changed in Settings and is saved", await sp.evaluate("document.documentElement.dir") == "ltr" and (await (await sp.context.request.get(PUB + "/api/auth/me")).json())["user"]["preferredLanguage"] == "en")
        await sp.wait_for_selector("main select"); await sp.locator("main select").nth(1).select_option("Asia/Dubai"); await sp.wait_for_selector("text=Saved.")
        check("the time zone is saved", (await (await sp.context.request.get(PUB + "/api/auth/me")).json())["user"]["timeZone"] == "Asia/Dubai")
        await sp.locator("main select").nth(2).select_option("dark")
        check("the dark theme applies at once", await sp.evaluate("document.documentElement.getAttribute('data-theme')") == "dark" and await sp.evaluate("getComputedStyle(document.body).backgroundColor") == "rgb(15, 23, 21)")
        await sp.reload(); await sp.wait_for_selector("main select")
        check("and is remembered after a reload", await sp.evaluate("document.documentElement.getAttribute('data-theme')") == "dark")
        await sp.locator("main select").nth(2).select_option("system")

        # ---- changing the password
        await sp.fill('section[aria-label="Password"] input[autocomplete="current-password"]', "not-my-password"); await sp.fill('section[aria-label="Password"] input[autocomplete="new-password"]', NEWPW)
        await sp.click("button:has-text('Change password')"); await sp.wait_for_selector("section[aria-label='Password'] .field-error")
        check("a wrong current password is refused next to its field", "not correct" in await sp.inner_text("section[aria-label='Password'] .field-error"))
        await sp.fill('section[aria-label="Password"] input[autocomplete="current-password"]', PW); await sp.click("button:has-text('Change password')"); await sp.wait_for_selector("section[aria-label='Password'] .ok-text")
        check("the right one changes it, and the person stays signed in", "password was changed" in await sp.inner_text("section[aria-label='Password'] .ok-text") and (await sp.context.request.get(PUB + "/api/auth/me")).status == 200)
        check("this person's other device is signed out", (await vp.context.request.get(PUB + "/api/auth/me")).status == 401)
        notice = await wait_mail(lambda m: m["to"] == rana and "password was changed" in m["subject"]); check("a warning e-mail says the password was changed", notice is not None, str(inbox[-3:]))

        # ---- disabling the account and getting it back by e-mail
        await sp.click("button:has-text('Disable my account…')"); await sp.fill('section[aria-label="Disable my account"] input[type="password"]', NEWPW)
        await sp.click("section[aria-label='Disable my account'] button.danger"); await sp.wait_for_url("**/login", timeout=15000)
        check("disabling signs the person out", (await sp.context.request.get(PUB + "/api/auth/me")).status == 401)
        await sp.fill('input[type="email"]', rana); await sp.fill('input[type="password"]', NEWPW); await sp.click("form button.btn"); await sp.wait_for_selector("[data-testid=account-disabled]")
        check("signing in tells the person the account is disabled and offers a link", "disabled" in (await sp.inner_text("[data-testid=account-disabled]")).lower())
        await sp.click("button:has-text('Email me the link')"); await sp.wait_for_selector("[data-testid=account-disabled] [role=status]")
        mail = await wait_mail(lambda m: m["to"] == rana and "Re-enable" in m["subject"]); check("the confirmation e-mail arrives", mail is not None, str(inbox[-3:]))
        link = __import__("re").search(r"https?://[^\s]+/reenable\?token=[A-Za-z0-9_-]+", mail["text"]).group(0).replace("http://localhost", PUB).replace("https://sayserne.test", PUB)
        await sp.goto(link); await sp.wait_for_selector("[role=status].ok-text"); check("opening the link switches the account back on", "switched on again" in await sp.inner_text("main"))
        await sp.goto(PUB + "/login"); await sp.fill('input[type="email"]', rana); await sp.fill('input[type="password"]', NEWPW); await sp.click("form button.btn"); await sp.wait_for_selector("text=Rana")
        check("and the person can sign in again", True)

        # ---- the back office: brand
        await adm.click("aside >> text=Brand"); await adm.wait_for_selector("h1:has-text('Brand')")
        await adm.wait_for_function("""document.querySelector('input[aria-label="Main colour (buttons and links) (#rrggbb)"]')?.value.startsWith('#')""")   # the saved values have loaded
        await adm.fill('input[aria-label="Main colour (buttons and links) (#rrggbb)"]', "red")
        await adm.wait_for_selector(".field-error"); check("a colour that is not #rrggbb is flagged and cannot be saved", await adm.locator("button:has-text('Save colours')").is_disabled())
        await adm.fill('input[aria-label="Main colour (buttons and links) (#rrggbb)"]', "#ffe066")
        await adm.wait_for_selector("[data-testid=contrast-warning]"); check("a pale main colour is flagged as hard to read", "contrast" in (await adm.inner_text("[data-testid=contrast-warning]")).lower())
        await adm.fill('input[aria-label="Main colour (buttons and links) (#rrggbb)"]', "#1a5fb4"); await adm.fill('input[aria-label="Darker shade (hovering over buttons) (#rrggbb)"]', "#124a8f")
        await adm.click("button:has-text('Save colours')"); await adm.wait_for_selector("[role=status]:has-text('Saved')")
        fresh = await (await b.new_context()).new_page(); await fresh.goto(PUB + "/"); await fresh.wait_for_selector("h1")
        check("the public site uses the new colour", (await fresh.evaluate("getComputedStyle(document.documentElement).getPropertyValue('--green')")).strip().lower() == "#1a5fb4")
        from PIL import Image, ImageDraw
        img = Image.new("RGB", (160, 48), (26, 95, 180)); ImageDraw.Draw(img).rectangle((8, 8, 40, 40), fill=(255, 255, 255)); img.save(os.path.join(tempfile.gettempdir(), "logo-e2e.png"))
        await adm.set_input_files("section[aria-label='Logo'] input[type=file]", os.path.join(tempfile.gettempdir(), "logo-e2e.png")); await adm.wait_for_selector("[role=status]:has-text('logo is set')")
        fresh = await (await b.new_context()).new_page(); await fresh.goto(PUB + "/"); await fresh.wait_for_selector("header .brand-logo"); check("an uploaded logo replaces the site name in the header", await fresh.locator("header .brand-logo").count() == 1)
        await adm.click("button:has-text('Remove logo')"); await adm.wait_for_selector("[role=status]:has-text('removed')")
        await adm.click("button:has-text('Use the original colours')"); await adm.wait_for_selector("[role=status]:has-text('original')")
        fresh = await (await b.new_context()).new_page(); await fresh.goto(PUB + "/"); await fresh.wait_for_selector("header .brand"); check("removing them brings the original look back", await fresh.locator("header .brand-logo").count() == 0 and (await fresh.evaluate("getComputedStyle(document.documentElement).getPropertyValue('--green')")).strip().lower() == "#2d6d5f")

        # ---- the back office in Arabic
        await adm.select_option("header select", "ar"); await adm.wait_for_function("document.documentElement.lang === 'ar'")
        check("the back office switches to Arabic, right-to-left", await adm.evaluate("document.documentElement.dir") == "rtl" and "المستخدمون" in await adm.inner_text("aside"))
        await adm.goto(ADM + "/"); await adm.wait_for_selector("[data-testid=dashboard]"); check("the dashboard is in Arabic", "المستخدمون" in await adm.inner_text("[data-testid=dashboard]"))
        await adm.select_option("header select", "en"); await adm.wait_for_function("document.documentElement.lang === 'en'")
        print("   page errors:", errors); check("no uncaught browser errors", not errors)
        await b.close()
    server.close()
asyncio.run(main())
print(f"\n{sum(results)}/{len(results)} phase 4 end-to-end checks passed")
