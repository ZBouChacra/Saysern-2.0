"""End-to-end journey 01_admin: drives the real public site (http://127.0.0.1:5173) and back office (http://127.0.0.1:5174) in a browser, against a running API and an empty database.
See tests/e2e/README.md for how to start everything. Environment: SAYSERNE_MIGRATOR_PROJECT (path to api/Sayserne.Migrator), SAYSERNE_MIGRATOR_CONNECTION (to promote the first admin),
E2E_MYSQL (default "mysql -uroot") and E2E_DB (default "sayserne_dev") for the few steps that move a date back in time."""
import tempfile
import os
import asyncio, base64, hashlib, hmac, os, struct, subprocess, time, json, uuid
from playwright.async_api import async_playwright, expect

PUB, ADM = "http://127.0.0.1:5173", "http://127.0.0.1:5174"
PW = "correct-horse-battery-9"
results = []
def check(name, cond, extra=""):
    results.append(bool(cond)); print(("PASS " if cond else "FAIL ") + name + (f"   [{extra}]" if (extra and not cond) else ""))

def totp(secret, step):
    key = base64.b32decode(secret + "=" * ((8 - len(secret) % 8) % 8))
    h = hmac.new(key, struct.pack(">Q", step), hashlib.sha1).digest()
    o = h[-1] & 15
    return f"{(struct.unpack('>I', h[o:o+4])[0] & 0x7fffffff) % 1000000:06d}"
def now_step(): return int(time.time() // 30)

def promote(email):
    subprocess.run(["dotnet", "run", "--no-build", "--project", os.environ.get("SAYSERNE_MIGRATOR_PROJECT", "api/Sayserne.Migrator"), "--", "promote-admin", email], env=os.environ, check=True, capture_output=True)

async def register_ui(browser, first, last, email, password=PW):
    ctx = await browser.new_context(); pg = await ctx.new_page()
    await pg.goto(PUB + "/register"); await pg.wait_for_selector("text=Create your account")
    await pg.fill('input[autocomplete="given-name"]', first); await pg.fill('input[autocomplete="family-name"]', last)
    await pg.fill('input[autocomplete="email"]', email); await pg.fill('input[autocomplete="new-password"]', password)
    await pg.click("form button.btn"); await pg.wait_for_selector(f"text=Welcome, {first}")
    await ctx.close()

async def main():
    uid = uuid.uuid4().hex[:6]
    admin_email, staff_email, cust_email = f"boss{uid}@example.test", f"staff{uid}@example.test", f"cust{uid}@example.test"
    async with async_playwright() as p:
        b = await p.chromium.launch()
        errors = []

        # ---------- public site basics driven by the database
        ctx = await b.new_context(viewport={"width": 1150, "height": 850}); pub = await ctx.new_page()
        pub.on("pageerror", lambda e: errors.append("public: " + str(e)))
        await pub.goto(PUB + "/register"); await pub.wait_for_selector("text=Create your account")
        hint = await pub.inner_text("p.hint")
        check("register hint comes from the server's settings (10 characters)", "10 characters" in hint and "letter and a digit" in hint, hint)
        await ctx.close()
        for first, email in [("Boss", admin_email), ("Staffer", staff_email), ("Carla", cust_email)]: await register_ui(b, first, "Tester", email)
        promote(admin_email)

        # ---------- back office: first sign-in = enrollment
        actx = await b.new_context(viewport={"width": 1250, "height": 900}); adm = await actx.new_page()
        adm.on("pageerror", lambda e: errors.append("admin: " + str(e)))
        await adm.goto(ADM + "/"); await adm.wait_for_selector("text=Back office")
        check("the admin app shows only a sign-in form to anonymous visitors", await adm.locator("text=Users").count() == 0)
        await adm.fill('input[type="email"]', admin_email); await adm.fill('input[type="password"]', "wrong-password-1"); await adm.click("button:has-text('Continue')")
        await adm.wait_for_selector("[role=alert]"); check("a wrong password is refused with a clear message", "incorrect" in (await adm.inner_text("[role=alert]")).lower())
        await adm.fill('input[type="password"]', PW); await adm.click("button:has-text('Continue')")
        await adm.wait_for_selector("[data-testid=totp-secret]")
        secret = await adm.inner_text("[data-testid=totp-secret]")
        check("first sign-in forces authenticator enrollment (QR + manual key)", len(secret) == 32 and await adm.locator('img[alt="Authenticator QR code"]').count() == 1)
        await adm.fill('input[autocomplete="one-time-code"]', "000000"); await adm.click("button:has-text('Confirm and continue')")
        await adm.wait_for_selector("[role=alert]"); check("a wrong enrollment code is refused", "not valid" in (await adm.inner_text("[role=alert]")).lower())
        used = now_step(); await adm.fill('input[autocomplete="one-time-code"]', totp(secret, used)); await adm.click("button:has-text('Confirm and continue')")
        await adm.wait_for_selector("[data-testid=recovery-codes]")
        codes = [await li.inner_text() for li in await adm.locator("[data-testid=recovery-codes] li").all()]
        check("ten recovery codes are shown once", len(codes) == 10 and all(len(c) == 11 for c in codes))
        await adm.click("button:has-text('I have saved them')"); await adm.wait_for_selector("text=Welcome, Boss")
        nav = await adm.inner_text("aside.side")
        check("a super admin sees every area", all(x in nav for x in ["Users", "Roles", "Settings", "Lookups", "Texts", "Catalog", "Audit log", "My security"]))

        # ---------- settings: change, see it in the public site, reset
        await adm.click("aside >> text=Settings"); await adm.wait_for_selector('[data-setting="Auth.PasswordMinLength"]')
        row = adm.locator('[data-setting="Auth.PasswordMinLength"]')
        await row.locator("input").fill("7"); await row.locator("button:has-text('Save')").click()
        await expect(row.locator("[role=status]")).to_contain_text("outside the allowed range"); check("a value below the security floor is refused", True)
        await row.locator("input").fill("12"); await row.locator("button:has-text('Save')").click()
        await expect(row.locator("[role=status]")).to_contain_text("Saved"); check("a valid setting change is saved", True)
        cfg = await adm.evaluate("fetch('/api/config/public', {cache: 'no-store'}).then(r => r.json())")
        check("the public config reflects the change immediately", cfg["Auth.PasswordMinLength"] == 12)
        bool_row = adm.locator('[data-setting="Auth.PasswordRequireDigit"]')
        check("a switch setting is rendered as on/off", await bool_row.locator("select").count() == 1)

        # ---------- the public site follows the setting without a redeploy
        ctx2 = await b.new_context(); pub2 = await ctx2.new_page()
        await pub2.goto(PUB + "/register"); await pub2.wait_for_selector("text=Create your account")
        check("the public register hint now says 12", "12 characters" in await pub2.inner_text("p.hint"))
        await pub2.fill('input[autocomplete="given-name"]', "A"); await pub2.fill('input[autocomplete="family-name"]', "B"); await pub2.fill('input[autocomplete="email"]', f"x{uid}@example.test")
        await pub2.fill('input[autocomplete="new-password"]', "abcdefghij1"); await pub2.click("form button.btn")
        await pub2.wait_for_selector(".field-error, [role=alert]"); check("an 11-character password is rejected by the form", "12 characters" in await pub2.inner_text("form"))
        await ctx2.close()

        # ---------- lookups: add a city in Arabic and English under Lebanon
        await adm.click("aside >> text=Lookups"); await adm.click("button.room:has-text('Cities')")
        await adm.select_option('select[aria-label="Parent"]', label="Lebanon"); await adm.wait_for_selector("td:has-text('Beirut')")
        shown = await adm.locator("tbody tr").count(); check("cities of Lebanon are listed with English and Arabic names", shown >= 15 and await adm.locator("td:has-text('بيروت')").count() >= 1, str(shown))
        await adm.click("button:has-text('Add value')"); city_code = f"LB-test{uid}"
        await adm.fill('div[role=dialog] label:has-text("Code") input', city_code); await adm.fill('div[role=dialog] label:has-text("Name (English)") input', f"Testville{uid}"); await adm.fill('div[role=dialog] label:has-text("Name (Arabic)") input', "تيستفيل")
        await adm.click("div[role=dialog] button:has-text('Save')"); await adm.wait_for_selector(f"td:has-text('Testville{uid}')")
        pub_cities = await adm.evaluate("fetch('/api/lookups/city?parent=LB', {cache: 'no-store'}).then(r => r.json())")
        check("the new city is served to visitors", any(c["code"] == city_code and c["names"]["ar"] == "تيستفيل" for c in pub_cities))

        # ---------- roles and staff
        await adm.click("aside >> text=Roles"); await adm.click("button:has-text('New role')")
        await adm.fill('div[role=dialog] label:has-text("Name") input', f"Reader {uid}"); await adm.check('div[role=dialog] label:has-text("users.read") input'); await adm.click("div[role=dialog] button:has-text('Save')")
        await adm.wait_for_selector(f"td:has-text('Reader {uid}')"); check("a role can be created with chosen permissions", True)
        await adm.click("aside >> text=Users"); await adm.fill('input[aria-label="Search users"]', staff_email); await adm.click("form.toolbar button:has-text('Search')")
        await adm.wait_for_selector(f"td:has-text('{staff_email}')"); await adm.click("button:has-text('Roles…')")
        await adm.check(f'div[role=dialog] label:has-text("Reader {uid}") input'); await adm.click("div[role=dialog] button:has-text('Save roles')")
        await adm.wait_for_selector(f"td:has-text('Reader {uid}')"); check("a role can be assigned to a user", True)

        # ---------- texts, catalog, audit
        await adm.click("aside >> text=Texts"); await adm.fill('input[aria-label="Search texts"]', "SLOT_TAKEN")
        box = adm.locator('textarea[aria-label="error.SLOT_TAKEN en"]'); await box.fill("Sorry, that slot just went."); await adm.locator("tr:has(code:text('error.SLOT_TAKEN')) button:has-text('Save')").first.click()
        await adm.wait_for_selector("[role=status]"); texts = await adm.evaluate("fetch('/api/translations/en', {cache: 'no-store'}).then(r => r.json())")
        check("an edited text is served immediately", texts["error.SLOT_TAKEN"] == "Sorry, that slot just went.")
        await adm.locator("tr:has(code:text('error.SLOT_TAKEN')) button:has-text('Reset')").first.click(); await adm.wait_for_timeout(500)
        check("a text can be reset to its shipped wording", (await adm.evaluate("fetch('/api/translations/en', {cache: 'no-store'}).then(r => r.json())"))["error.SLOT_TAKEN"].startswith("That time is no longer"))
        await adm.click("aside >> text=Catalog"); await adm.click("section:has(h2:text('Categories')) >> button:has-text('Add')")
        await adm.fill('div[role=dialog] label:has-text("Name (English)") input', f"Cat{uid}"); await adm.fill('div[role=dialog] label:has-text("Name (Arabic)") input', "فئة"); await adm.click("div[role=dialog] button:has-text('Save')")
        await adm.wait_for_selector(f"button.room:has-text('Cat{uid}')"); await adm.click("button:has-text('Add service')")
        await adm.fill('div[role=dialog] label:has-text("Name (English)") input', f"Svc{uid}"); await adm.click("div[role=dialog] button:has-text('Save')"); await adm.wait_for_selector(f"td:has-text('Svc{uid}')")
        cats = await adm.evaluate("fetch('/api/catalog/categories', {cache: 'no-store'}).then(r => r.json())"); check("a new category and service reach the public catalog", any(c["name"] == f"Cat{uid}" for c in cats))
        await adm.click("aside >> text=Audit log"); await adm.fill('input[aria-label="Action"]', "settings.update"); await adm.click("form.toolbar button:has-text('Filter')")
        await adm.wait_for_selector("td code:text('settings.update')"); check("the audit log shows who changed the setting", await adm.locator("td:has-text('Boss')").count() >= 1)
        await adm.click("button:has-text('Details')"); check("an audit entry shows the old and new value", "12" in await adm.inner_text("pre.json"))

        # ---------- sign out, sign in again (next time step), wrong code, recovery code
        await adm.click("button:has-text('Sign out')"); await adm.wait_for_selector("text=Back office"); used = max(used + 1, now_step() - 1)
        await adm.fill('input[type="email"]', admin_email); await adm.fill('input[type="password"]', PW); await adm.click("button:has-text('Continue')")
        await adm.wait_for_selector("text=6-digit code"); await adm.fill('input[autocomplete="one-time-code"]', totp(secret, used - 5)); await adm.click("button:has-text('Sign in')")
        await adm.wait_for_selector("[role=alert]"); check("a stale authenticator code is refused", "not valid" in (await adm.inner_text("[role=alert]")).lower())
        await adm.fill('input[autocomplete="one-time-code"]', totp(secret, used)); await adm.click("button:has-text('Sign in')")
        await adm.wait_for_selector("text=Welcome, Boss"); check("signing in again works with the next authenticator code", True)
        await adm.click("button:has-text('Sign out')"); await adm.wait_for_selector("text=Back office")
        await adm.fill('input[type="email"]', admin_email); await adm.fill('input[type="password"]', PW); await adm.click("button:has-text('Continue')")
        await adm.wait_for_selector("text=6-digit code"); await adm.click("button:has-text('lost my device')"); await adm.fill('input[autocomplete="one-time-code"]', codes[0]); await adm.click("button:has-text('Sign in')")
        await adm.wait_for_selector("text=Welcome, Boss"); check("a recovery code signs in", True)
        await adm.click("aside >> text=My security"); check("the spent recovery code is counted", (await adm.inner_text("[data-testid=recovery-remaining]")).strip() == "9")
        await actx.close()

        # ---------- staff with a limited role
        sctx = await b.new_context(); stf = await sctx.new_page()
        await stf.goto(ADM + "/"); await stf.wait_for_selector("text=Back office")
        await stf.fill('input[type="email"]', staff_email); await stf.fill('input[type="password"]', PW); await stf.click("button:has-text('Continue')")
        await stf.wait_for_selector("[data-testid=totp-secret]"); ssecret = await stf.inner_text("[data-testid=totp-secret]")
        await stf.fill('input[autocomplete="one-time-code"]', totp(ssecret, now_step())); await stf.click("button:has-text('Confirm and continue')"); await stf.wait_for_selector("[data-testid=recovery-codes]"); await stf.click("button:has-text('I have saved them')")
        await stf.wait_for_selector("text=Welcome, Staffer"); snav = await stf.inner_text("aside.side")
        check("staff see only the areas their role allows", "Users" in snav and "My security" in snav and "Settings" not in snav and "Roles" not in snav and "Audit" not in snav, snav.replace("\n", "|"))
        await stf.goto(ADM + "/settings"); await stf.wait_for_selector("text=Not allowed"); check("a direct link to a forbidden area shows 'Not allowed'", True)
        api_status = await stf.evaluate("fetch('/api/admin/settings', {credentials:'same-origin'}).then(r => r.status)")
        check("…and the API itself refuses (403)", api_status == 403, str(api_status))
        await sctx.close()

        # ---------- public site: lookups, settings, language, no back office
        cctx = await b.new_context(viewport={"width": 1150, "height": 900}); cust = await cctx.new_page()
        cust.on("pageerror", lambda e: errors.append("customer: " + str(e)))
        await cust.goto(PUB + "/login"); await cust.fill('input[type="email"]', cust_email); await cust.fill('input[type="password"]', PW); await cust.click("form button.btn"); await cust.wait_for_selector("text=Welcome, Carla")
        check("the public site has no back-office link", await cust.locator("text=Back office").count() == 0)
        anon = await (await b.new_context()).new_page()
        await anon.goto(PUB + "/login?error=login_required"); await anon.wait_for_selector("[role=alert]")
        check("redirect messages on first render use the database texts", "Please sign in first" in await anon.inner_text("[role=alert]"), await anon.inner_text("[role=alert]"))
        await cust.goto(PUB + "/me"); await cust.wait_for_selector("main >> text=Country")
        country_opts = await cust.locator("main label:has-text('Country') select option").all_inner_texts()
        check("the country list comes from the lookup (171 countries)", len(country_opts) == 172 and "Lebanon" in country_opts, str(len(country_opts)))
        nat_opts = await cust.locator("label:has-text('Nationality') select option").all_inner_texts(); check("nationalities come from the lookup (74)", len(nat_opts) == 75 and "Lebanese" in nat_opts, str(len(nat_opts)))
        await cust.select_option("main label:has-text('Country') select", "LB"); await cust.select_option("label:has-text('Nationality') select", "lebanese"); await cust.select_option("label:has-text('Sex') select", "female")
        await cust.wait_for_timeout(500); dl = await cust.locator("datalist option").evaluate_all("els => els.map(e => e.value)")
        check("city suggestions follow the chosen country (incl. the one the admin just added)", "Beirut" in dl and f"Testville{uid}" in dl, str(len(dl)))
        await cust.fill("main label:has-text('City') input", "Beirut"); await cust.click("main button:has-text('Save')"); await cust.wait_for_selector(".ok-text, [role=status]"); await cust.reload(); await cust.wait_for_selector("main >> text=Country")
        check("the profile saves lookup codes and shows them again", await cust.input_value("main label:has-text('Country') select") == "LB" and await cust.input_value("label:has-text('Nationality') select") == "lebanese")

        # a professional (set up through the API) so the booking screen can be checked
        api = await p.request.new_context(base_url=PUB, extra_http_headers={"Origin": PUB})
        pro_email = f"pro{uid}@example.test"
        r = await api.post("/api/auth/register", data={"email": pro_email, "password": PW, "firstName": "Pia", "lastName": "Pro"}); assert r.status == 201, await r.text()
        await api.put("/api/me/profile", data={"firstName": "Pia", "lastName": "Pro", "profileType": "professional", "country": "LB", "city": "Beirut"})
        svc = (await (await api.get("/api/catalog/services")).json())[0]
        pr = await api.post("/api/me/professions", data={"categoryId": svc["categoryId"], "serviceId": svc["id"], "city": "Beirut", "country": "LB", "costPerHour": 50}); assert pr.status == 201, await pr.text()
        pro_id = (await (await api.get("/api/auth/me")).json())["user"]["id"]
        await cust.goto(f"{PUB}/book/{pro_id}"); await cust.wait_for_selector("label:has-text('Duration') select")
        durations = await cust.locator("label:has-text('Duration') select option").all_inner_texts(); check("booking lengths come from the lookup", durations == ["30 minutes", "45 minutes", "1 hour", "1.5 hours", "2 hours", "3 hours"], str(durations))
        # admin narrows the booking window; the public screen follows without a redeploy
        actx2 = await b.new_context(); a2 = await actx2.new_page()
        await a2.goto(ADM + "/"); await a2.fill('input[type="email"]', admin_email); await a2.fill('input[type="password"]', PW); await a2.click("button:has-text('Continue')"); await a2.wait_for_selector("text=6-digit code")
        await a2.click("button:has-text('lost my device')"); await a2.fill('input[autocomplete="one-time-code"]', codes[1]); await a2.click("button:has-text('Sign in')"); await a2.wait_for_selector("text=Welcome, Boss")
        await a2.click("aside >> text=Settings"); r2 = a2.locator('[data-setting="Booking.MaxDurationMinutes"]'); await r2.locator("input").fill("60"); await r2.locator("button:has-text('Save')").click(); await expect(r2.locator("[role=status]")).to_contain_text("Saved")
        cfg_now = await cust.evaluate("fetch('/api/config/public', {cache: 'no-store'}).then(r => r.json())")
        check("the API publishes the lowered maximum at once", cfg_now["Booking.MaxDurationMinutes"] == 60, str(cfg_now.get("Booking.MaxDurationMinutes")))
        fresh = await b.new_context(); fp = await fresh.new_page()   # a visitor with nothing cached (browsers keep the settings for up to 10 seconds)
        await fp.goto(PUB + "/login"); await fp.fill('input[type="email"]', cust_email); await fp.fill('input[type="password"]', PW); await fp.click("form button.btn"); await fp.wait_for_selector("text=Welcome, Carla")
        await fp.goto(f"{PUB}/book/{pro_id}"); await fp.wait_for_selector("label:has-text('Duration') select")
        durations = await fp.locator("label:has-text('Duration') select option").all_inner_texts(); check("after the admin lowers the maximum, longer lengths disappear", durations == ["30 minutes", "45 minutes", "1 hour"], str(durations))
        await fresh.close()
        await actx2.close()
        # search page filters
        await cust.goto(PUB + "/search"); await cust.wait_for_selector("main label:has-text('Country') select"); await cust.select_option("main label:has-text('Country') select", "LB"); await cust.click("button:has-text('Search')")
        await cust.wait_for_timeout(1500); await cust.wait_for_selector("a.listing"); await cust.wait_for_timeout(500)
        check("search offers lookup-driven filters and finds the professional", await cust.locator("a.listing").count() >= 1 and "Pia" in await cust.inner_text(".list"), await cust.inner_text("main"))
        # language: Arabic text + right-to-left
        await cust.evaluate("localStorage.setItem('sayserne.lang','ar')"); await cust.context.clear_cookies(); await cust.goto(PUB + "/login"); await cust.wait_for_selector("form")
        check("choosing Arabic switches the page to right-to-left", await cust.evaluate("document.documentElement.dir") == "rtl")
        await cust.fill('input[type="email"]', cust_email); await cust.fill('input[type="password"]', "wrong-password-xx"); await cust.click("form button.btn"); await cust.wait_for_selector("[role=alert]")
        msg = await cust.inner_text("[role=alert]"); check("error messages arrive in Arabic from the database", "غير صحيحة" in msg, msg)
        await cust.goto(PUB + "/admin"); await cust.wait_for_timeout(500); check("/admin on the public site is not a back office", "Users" not in await cust.inner_text("body") or "Lock" not in await cust.inner_text("body"))

        print("   page errors:", errors); check("no uncaught browser errors", not errors)
        await b.close()
asyncio.run(main())
print(f"\n{sum(results)}/{len(results)} end-to-end checks passed")
