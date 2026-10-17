"""End-to-end journey 09_contact_dashboard: drives the real public site (http://127.0.0.1:5173) and back office (http://127.0.0.1:5174) in a browser, against a running API and an empty database.
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
    uid = uuid.uuid4().hex[:6]; boss = f"boss{uid}@example.test"; visitor = f"visitor{uid}@example.test"; subject = f"Listing question {uid}"
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

        # ---- a visitor: footer, content pages, contact form
        v = await b.new_context(viewport={"width": 1150, "height": 900}); vp = await v.new_page(); vp.on("pageerror", lambda e: errors.append("visitor: " + str(e)))
        await vp.goto(PUB + "/"); await vp.wait_for_selector("footer nav")
        check("every page has a footer with About, Terms, Privacy and Contact", [t.lower() for t in await vp.locator("footer nav a").all_inner_texts()] == ["about", "terms", "privacy", "contact"])
        await vp.click("footer nav a:has-text('About')"); await vp.wait_for_selector("h1:has-text('About us')")
        check("the About page shows its headings and lists from the admin-written text", await vp.locator("h2:has-text('For customers')").count() == 1 and await vp.locator("main ul li").count() >= 3)
        await vp.click("footer nav a:has-text('Terms')"); await vp.wait_for_selector("h1:has-text('Terms of use')"); check("the Terms page says it is a draft to be reviewed", "DRAFT" in await vp.inner_text("main"))
        await vp.click("footer nav a:has-text('Privacy')"); await vp.wait_for_selector("h1:has-text('Privacy policy')"); check("the Privacy page describes what is and is not kept", "IP address" in await vp.inner_text("main"))
        await vp.click("footer nav a:has-text('Contact')"); await vp.wait_for_selector("h1:has-text('Contact us')")
        box = await vp.locator(".hp input").bounding_box(); check("the spam trap is off-screen and tiny, so people never see it", box is None or box["x"] < -1000 or box["width"] <= 2, str(box))
        await vp.fill("label:has-text('Your name') input", "Mona"); await vp.fill("label:has-text('Email') input", visitor); await vp.fill("label:has-text('Subject') input", subject)
        await vp.fill("label:has-text('Message') textarea", "How do I list my services in two cities?")
        await vp.click("button:has-text('Send message')"); await vp.wait_for_selector("[role=status]:has-text('We received your message')"); check("the form confirms the message was received", True)
        staff = await wait_mail(lambda m: m["to"] == "staff@sayserne.test" and subject in m["subject"]); check("staff are told by email that a message arrived", staff is not None and "two cities" in staff["text"], str(inbox))

        # ---- the admin: dashboard tile, inbox, reply
        await adm.goto(ADM + "/"); await adm.wait_for_selector("[data-testid=dashboard]")
        tile = adm.locator(".tile:has-text('Unanswered messages')"); check("the dashboard shows one unanswered message", (await tile.locator(".tile-value").inner_text()).strip() == "1")
        await tile.click(); await adm.wait_for_selector(f"tr:has-text('{subject}')"); await adm.locator(f"tr:has-text('{subject}') >> button:has-text('Open')").click(); await adm.wait_for_selector("[data-testid=contact-detail]")
        check("staff see who wrote and what", visitor in await adm.inner_text("[data-testid=contact-detail]") and "two cities" in await adm.inner_text("[data-testid=contact-detail]"))
        await adm.fill("[data-testid=contact-detail] textarea >> nth=0", "Open your profile and add a service for each city."); await adm.click("button:has-text('Send reply')")
        await adm.wait_for_selector(".reply-box"); check("the reply is recorded on the message", "Open your profile" in await adm.inner_text(".reply-box"))
        reply = await wait_mail(lambda m: m["to"] == visitor and m["subject"] == "Re: " + subject)
        check("the person receives the reply by email, with their original message", reply is not None and "add a service for each city" in reply["text"] and "two cities" in reply["text"], str(inbox[-2:]))
        await adm.goto(ADM + "/"); await adm.wait_for_selector("[data-testid=dashboard]"); await adm.wait_for_timeout(300)
        check("the dashboard tile drops to zero once it is answered", (await adm.locator(".tile:has-text('Unanswered messages') .tile-value").inner_text()).strip() == "0")

        # ---- the year view, its spreadsheet, and a user's details
        await adm.wait_for_selector("[data-testid=year-table] tbody tr"); check("the dashboard has a month-by-month table for the year", await adm.locator("[data-testid=year-table] tbody tr").count() == 12)
        href = await adm.get_attribute("[data-testid=year-table] a[download]", "href"); resp = await adm.context.request.get(ADM + href)
        body = await resp.body(); check("its spreadsheet downloads as CSV, readable by Excel", resp.status == 200 and "text/csv" in resp.headers.get("content-type", "") and body[:3] == b"\xef\xbb\xbf" and body.count(b"\r\n") == 13, href)
        await adm.click("aside >> text=Users"); await adm.fill('input[aria-label="Search users"]', boss) if await adm.locator('input[aria-label="Search users"]').count() else None
        await adm.locator(f"tr:has-text('{boss}') >> button:text-is('Details')").click(); await adm.wait_for_selector("[data-testid=user-detail] b")
        detail = await adm.inner_text("[data-testid=user-detail]"); check("a user's details open, with a note that it is audited", boss in detail and "audit log" in detail.lower(), detail[:200])
        audit = await (await adm.context.request.get(ADM + "/api/admin/audit?action=users.view", headers={"X-Requested-With": "sayserne-admin"})).json()
        check("and the opening was recorded", audit["total"] >= 1)

        # ---- editing a page through the Texts screen
        await adm.click("aside >> text=Texts"); await adm.fill('input[aria-label="Search texts"]', "page.about.title")
        await adm.locator('textarea[aria-label="page.about.title en"]').fill("About SaySern\u00e9: who we are"); await adm.locator("tr:has-text('page.about.title') >> button:text-is('Save')").first.click(); await adm.wait_for_selector("[role=status]")
        v2 = await b.new_context(); vp2 = await v2.new_page(); await vp2.goto(PUB + "/about"); await vp2.wait_for_selector("h1")
        check("an edited page text appears on the site", "who we are" in await vp2.inner_text("h1"), await vp2.inner_text("h1"))
        print("   page errors:", errors); check("no uncaught browser errors", not errors)
        await b.close()
    server.close()
asyncio.run(main())
print(f"\n{sum(results)}/{len(results)} contact, content and dashboard end-to-end checks passed")
