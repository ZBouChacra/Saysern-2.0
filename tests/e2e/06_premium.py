"""End-to-end journey 06_premium: drives the real public site (http://127.0.0.1:5173) and back office (http://127.0.0.1:5174) in a browser, against a running API and an empty database.
See tests/e2e/README.md for how to start everything. Environment: SAYSERNE_MIGRATOR_PROJECT (path to api/Sayserne.Migrator), SAYSERNE_MIGRATOR_CONNECTION (to promote the first admin),
E2E_MYSQL (default "mysql -uroot") and E2E_DB (default "sayserne_dev") for the few steps that move a date back in time."""
import tempfile
import os
import asyncio, base64, hashlib, hmac, os, struct, subprocess, time, uuid
from playwright.async_api import async_playwright

PUB, ADM = "http://127.0.0.1:5173", "http://127.0.0.1:5174"
PW = "correct-horse-battery-9"
results = []
def check(name, cond, extra=""):
    results.append(bool(cond)); print(("PASS " if cond else "FAIL ") + name + (f"   [{extra}]" if (extra and not cond) else ""))
def totp(secret, step):
    key = base64.b32decode(secret + "=" * ((8 - len(secret) % 8) % 8)); h = hmac.new(key, struct.pack(">Q", step), hashlib.sha1).digest(); o = h[-1] & 15
    return f"{(struct.unpack('>I', h[o:o+4])[0] & 0x7fffffff) % 1000000:06d}"
day = lambda n: time.strftime("%Y-%m-%d", time.gmtime(time.time() + 86400 * n))

async def main():
    uid = uuid.uuid4().hex[:6]; boss = f"boss{uid}@example.test"; last = f"Premiumtest{uid}"
    async with async_playwright() as p:
        b = await p.chromium.launch(); errors = []
        ctx = await b.new_context(); pg = await ctx.new_page(); await pg.goto(PUB + "/register"); await pg.wait_for_selector("text=Create your account")
        await pg.fill('input[autocomplete="given-name"]', "Boss"); await pg.fill('input[autocomplete="family-name"]', "Tester"); await pg.fill('input[autocomplete="email"]', boss)
        await pg.fill('input[autocomplete="new-password"]', PW); await pg.click("form button.btn"); await pg.wait_for_selector("text=Welcome, Boss"); await ctx.close()
        subprocess.run(["dotnet","run","--no-build","--project",os.environ.get("SAYSERNE_MIGRATOR_PROJECT", "api/Sayserne.Migrator"),"--","promote-admin",boss], env=os.environ, check=True, capture_output=True)

        # three professionals who differ only by place
        async def pro(first, country, city):
            api = await p.request.new_context(base_url=PUB, extra_http_headers={"Origin": PUB})
            r = await api.post("/api/auth/register", data={"email": f"{first.lower()}{uid}@example.test", "password": PW, "firstName": first, "lastName": last}); assert r.status == 201, await r.text()
            assert (await api.put("/api/me/profile", data={"firstName": first, "lastName": last, "profileType": "professional", "country": country, "city": city})).status == 200
            svc = (await (await api.get("/api/catalog/services")).json())[0]
            assert (await api.post("/api/me/professions", data={"categoryId": svc["categoryId"], "serviceId": svc["id"], "city": city, "country": country, "costPerHour": 50, "yearsOfExperience": 5})).status == 201
        await pro("Alpha", "LB", "Beirut"); await pro("Bravo", "LB", "Jounieh"); await pro("Charlie", "FR", "Paris")

        actx = await b.new_context(viewport={"width": 1350, "height": 1000}); adm = await actx.new_page(); adm.on("pageerror", lambda e: errors.append("admin: " + str(e)))
        await adm.goto(ADM + "/"); await adm.fill('input[type="email"]', boss); await adm.fill('input[type="password"]', PW); await adm.click("button:has-text('Continue')")
        await adm.wait_for_selector("[data-testid=totp-secret]"); secret = await adm.inner_text("[data-testid=totp-secret]")
        await adm.fill('input[autocomplete="one-time-code"]', totp(secret, int(time.time() // 30))); await adm.click("button:has-text('Confirm and continue')"); await adm.wait_for_selector("[data-testid=recovery-codes]"); await adm.click("button:has-text('I have saved them')")
        await adm.wait_for_selector("text=Welcome, Boss")

        # ---- a limit of one premium listing per day in Lebanon
        await adm.click("aside >> text=Premium"); await adm.wait_for_selector("text=Prices and limits")
        sec = adm.locator("section[aria-label='Premium prices and limits']")
        await sec.locator('select[aria-label="Premium country"]').select_option(label="Lebanon"); await sec.locator('input[aria-label="Premium fee per day"]').fill("4"); await sec.locator('input[aria-label="Premium per day"]').fill("1")
        await sec.locator("button:has-text('Save')").click(); await sec.locator("td:has-text('whole country')").wait_for()
        await sec.locator("button:has-text('What applies here?')").click(); await adm.wait_for_selector("[data-testid=premium-price-check]")
        t = await adm.inner_text("[data-testid=premium-price-check]"); check("a country limit and fee are saved and explained", "4.00 USD per day" in t and "at most 1 premium" in t, t)

        async def book(first, frm=0, to=4):
            bk = adm.locator("section[aria-label='Book premium']")
            await bk.locator('input[aria-label="Find a listing"]').fill(f"{first} {last}"); await bk.locator("button:has-text('Find')").click()
            await bk.locator("ul[aria-label='Listings found'] button").first.click()
            await bk.locator('input[aria-label="Premium from"]').fill(day(frm)); await bk.locator('input[aria-label="Premium to"]').fill(day(to)); await bk.locator("button:has-text('Check the days')").click()
            await bk.locator("[data-testid=premium-preview]").wait_for(); text = (await bk.locator("[data-testid=premium-preview]").inner_text()).replace("\n", " ")
            await bk.locator("button:has-text('Reserve')").click(); await adm.locator(f"tr:has-text('{first} {last}') >> span.pill:has-text('pending')").wait_for()
            return text
        async def pay(first):
            row = adm.locator(f"tr:has-text('{first} {last}')"); await row.locator("button:has-text('Record payment')").click()
            await adm.locator("section[aria-label='Record payment'] button:has-text('Record payment and issue receipt')").click(); await row.locator("span.pill.ok:has-text('paid')").wait_for(); return await row.inner_text()

        text = await book("Alpha"); check("the preview says every day is free and prices the batch", "Every day has a free premium slot" in text and "20.00 USD" in text, text)
        row = await pay("Alpha"); check("a paid batch runs for all its days and is premium today", "5 of 5 days" in row and "premium today" in row and "pushed" not in row, row)
        text = await book("Bravo"); check("the preview warns that the days are already full", "5 day(s) are already full" in text, text)
        row = await pay("Bravo"); check("the second payer is pushed later by exactly the days it lost, and is not premium today", "5 of 5 days" in row and "pushed +5 days" in row and "premium today" not in row, row)

        # ---- what visitors see
        async def order(country_label=None, city=None):
            v = await b.new_context(viewport={"width": 1250, "height": 900}); pgv = await v.new_page(); pgv.on("pageerror", lambda e: errors.append("visitor: " + str(e)))
            await pgv.goto(PUB + "/"); await pgv.wait_for_selector("details.place-picker")
            if country_label:
                await pgv.click("details.place-picker summary"); await pgv.select_option(".place-pop select", label=country_label)
                if city: await pgv.fill(".place-pop input", city)
                await pgv.click(".place-pop button:has-text('Save')")
            await pgv.goto(PUB + f"/search?lastName={last}"); await pgv.wait_for_selector("a.listing")
            texts = await pgv.locator("a.listing").all_inner_texts(); prem = await pgv.locator("a.listing").first.inner_text(); await v.close()
            return [n for t in texts for n in ("Alpha", "Bravo", "Charlie") if n in t], prem
        o, first_card = await order("Lebanon", "Jounieh"); check("premium first; then the visitor's own country: a Lebanese visitor sees Bravo before Charlie", o == ["Alpha", "Bravo", "Charlie"] and "premium" in first_card.lower(), str(o))
        o, _ = await order("France", "Paris"); check("a visitor in France sees their own country's listing before the other non-premium one", o == ["Alpha", "Charlie", "Bravo"], str(o))
        o, _ = await order(); check("with no place the premium listing is still first and nobody is hidden", o[0] == "Alpha" and sorted(o) == ["Alpha", "Bravo", "Charlie"], str(o))

        # ---- the first payer cancels: the second moves up
        await adm.locator("tr:has-text('Alpha') >> button:has-text('Cancel')").click(); await adm.locator("tr:has-text('Alpha') >> span.pill.bad:has-text('cancelled')").wait_for()
        await adm.wait_for_timeout(300); await adm.reload(); await adm.wait_for_selector(f"tr:has-text('Bravo {last}')")
        row = await adm.locator(f"tr:has-text('Bravo {last}')").inner_text(); check("when the first payer cancels, the next batch moves up and is premium today", "premium today" in row and "pushed" not in row, row)

        # ---- the old premium switch is gone from the Users page; the featured star stays
        await adm.click("aside >> text=Users"); await adm.wait_for_selector("button:has-text('Star')", timeout=30000); html = await adm.inner_text("main")
        check("the Users page has no premium switch any more, only the featured star", await adm.locator("button:text-matches('premium', 'i')").count() == 0 and await adm.locator("button:has-text('Star')").count() >= 1, html[:200])
        print("   page errors:", errors); check("no uncaught browser errors", not errors)
        await b.close()
asyncio.run(main())
print(f"\n{sum(results)}/{len(results)} premium end-to-end checks passed")
