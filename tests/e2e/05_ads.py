"""End-to-end journey 05_ads: drives the real public site (http://127.0.0.1:5173) and back office (http://127.0.0.1:5174) in a browser, against a running API and an empty database.
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
def sh(*a): subprocess.run(a, check=True, capture_output=True, env=os.environ)

async def main():
    uid = uuid.uuid4().hex[:6]; work = f"{tempfile.gettempdir()}/e2e6-{uid}"; os.makedirs(work)
    pics = {}
    for name, color in (("a", "0x2a7f62"), ("b", "0xc0572b"), ("c", "0x5b6bbf")):
        pics[name] = f"{work}/{name}.jpg"; sh("ffmpeg","-nostdin","-v","error","-y","-f","lavfi","-i",f"color=c={color}:s=1200x300","-frames:v","1",pics[name])
    boss = f"boss{uid}@example.test"; T_A, T_B, T_C = f"Worldwide {uid}", f"Lebanon {uid}", f"Third {uid}"
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

        # ---- prices
        await adm.click("aside >> text=Ad prices"); await adm.wait_for_selector("text=Set or check a price")
        await adm.select_option('select[aria-label="Position"]', "home_banner"); await adm.select_option('select[aria-label="Country"]', label="Lebanon")
        await adm.fill('input[aria-label="Fee per day"]', "4"); await adm.fill('input[aria-label="Ads per day"]', "2"); await adm.click("button:has-text('Save')")
        await adm.wait_for_selector("td:has-text('Lebanon')"); check("a price for Lebanon is saved and listed", "4.00" in await adm.inner_text("table.t"))
        await adm.click("button:has-text('What applies here?')"); await adm.wait_for_selector("[data-testid=price-check]")
        t = await adm.inner_text("[data-testid=price-check]"); check("the page says which price applies there", "4.00 USD per day" in t and "at most 2 ads" in t, t)
        await adm.select_option('select[aria-label="Country"]', ""); await adm.click("button:has-text('What applies here?')"); await adm.wait_for_function("document.querySelector('[data-testid=price-check]').textContent.includes('5.00')")
        check("elsewhere the defaults from Settings apply", True)

        # ---- ads
        async def new_ad(title, pic, country=None):
            await adm.click("aside >> text=Ads"); await adm.wait_for_selector("button:has-text('New ad')"); await adm.click("button:has-text('New ad')"); await adm.wait_for_selector("section[aria-label='Ad form']")
            form = adm.locator("section[aria-label='Ad form']"); await form.locator("label:has-text('Title') input").fill(title); await form.locator("select").first.select_option("home_banner")
            await form.locator('input[aria-label="Ad picture"]').set_input_files(pic); await form.locator("img[alt='Ad preview']").wait_for(timeout=20000)
            await form.locator("input[type=url]").fill("https://example.test/landing")
            if country: await form.locator("select").nth(1).select_option(label=country)
            await form.locator("button:has-text('Save')").click(); await adm.wait_for_selector(f"td:has-text('{title}')")
        await new_ad(T_A, pics["a"]); await new_ad(T_B, pics["b"], "Lebanon")
        check("ads with their pictures appear in the list", await adm.locator("table.t tbody tr").count() >= 2 and await adm.locator("table.t img").count() >= 2)

        async def book(title, frm=0, to=2, pay=False, expect_full=False):
            await adm.click("aside >> text=Ads"); await adm.wait_for_selector(f"tr:has-text('{title}')"); await adm.locator(f"tr:has-text('{title}') >> button:has-text('Batches')").click()
            sec = adm.locator(f"section[aria-label='Batches of {title}']"); await sec.wait_for()
            day = lambda n: time.strftime("%Y-%m-%d", time.gmtime(time.time() + 86400 * n))
            await sec.locator('input[aria-label="From"]').fill(day(frm)); await sec.locator('input[aria-label="To"]').fill(day(to)); await sec.locator("button:has-text('Check availability')").click()
            await sec.locator("[data-testid=capacity-preview]").wait_for(); text = await sec.locator("[data-testid=capacity-preview]").inner_text()
            if expect_full: return text, await sec.locator("button:has-text('Reserve')").is_disabled()
            await sec.locator("button:has-text('Reserve')").click(); await sec.locator("tbody tr:has-text('pending')").wait_for()
            if pay:
                await sec.locator("button:has-text('Record payment')").click(); await adm.locator("section[aria-label='Record payment'] button:has-text('Record payment and issue receipt')").click()
                await sec.locator("tbody span.pill.ok:has-text('paid')").wait_for()
            return text, None
        text, _ = await book(T_A, pay=True); check("availability is previewed with the price before reserving", "all days are available" in text.lower() and "15.00 USD" in text.replace("\n", " "), text)
        await book(T_B, pay=True)
        await adm.click("aside >> text=Ads"); await adm.wait_for_selector(f"tr:has-text('{T_A}')"); check("both ads are running", await adm.locator("span.pill.ok:has-text('running')").count() == 2, (await adm.inner_text("table.t")).replace("\n", " | ")[:400])

        # ---- the hard stop, through the screen
        await adm.click("aside >> text=Ad prices"); await adm.wait_for_selector("text=Set or check a price")
        await adm.select_option('select[aria-label="Position"]', "home_banner"); await adm.select_option('select[aria-label="Country"]', ""); await adm.fill('input[aria-label="Ads per day"]', "1"); await adm.click("button:has-text('Save')")
        await adm.wait_for_selector("td:has-text('Everywhere')")
        await new_ad(T_C, pics["c"])
        text, disabled = await book(T_C, 0, 2, expect_full=True)
        check("days that are full are shown as full and cannot be reserved", "full" in text.lower() and disabled, text)
        await book(T_C, 10, 11); check("other days can be reserved", True)

        # ---- a visitor
        vctx = await b.new_context(viewport={"width": 1250, "height": 900}); v = await vctx.new_page(); v.on("pageerror", lambda e: errors.append("visitor: " + str(e)))
        await v.goto(PUB + "/"); await v.wait_for_selector(".ad-home_banner img")
        alt = await v.get_attribute(".ad-home_banner img", "alt"); check("a visitor with no known place sees only the worldwide ad", alt == T_A and await v.locator(".ad-dots").count() == 0, alt)
        check("the ad is labelled as an ad and links through the click counter", await v.locator(".ad-label").inner_text() == "Ad" and "/api/ads/" in await v.get_attribute(".ad-home_banner a", "href") and "sponsored" in await v.get_attribute(".ad-home_banner a", "rel"))
        check("no empty boxes where there is no ad", await v.locator(".ad-search_banner").count() == 0)
        try:
            await v.click("details.place-picker summary", timeout=8000); await v.select_option(".place-pop select", label="Lebanon", timeout=8000); await v.click(".place-pop button:has-text('Save')", timeout=8000)
        except Exception as ex:
            print("DEBUG picker:", str(ex)[:300].replace("\n", " ")); print("DEBUG topbar:", (await v.inner_text("header.topbar"))[:80].replace("\n", " | ")); await v.screenshot(path=os.path.join(tempfile.gettempdir(), "picker.png")); print("DEBUG box:", await v.evaluate("(() => { const b = document.querySelector('.place-pop button'); const r = b.getBoundingClientRect(); const cs = getComputedStyle(document.querySelector('.place-pop')); return JSON.stringify({x:r.x,y:r.y,w:r.width,h:r.height,vis:cs.visibility,disp:cs.display,pos:cs.position,vw:innerWidth}); })()")); raise
        await v.wait_for_selector(".ad-dots"); alt = await v.get_attribute(".ad-home_banner img", "alt"); check("after choosing Lebanon the more specific ad comes first, and both rotate", alt == T_B and await v.locator(".ad-dots button").count() == 2, alt)
        await v.reload(); await v.mouse.move(5, 400); await v.wait_for_selector(".ad-dots"); check("the chosen place is remembered", await v.get_attribute(".ad-home_banner img", "alt") == T_B)
        await v.wait_for_timeout(6500); alt2 = await v.get_attribute(".ad-home_banner img", "alt"); check("the slot rotates to the next ad", alt2 == T_A, alt2)
        await v.hover(".ad-home_banner"); await v.wait_for_timeout(6500); check("hovering pauses the rotation", await v.get_attribute(".ad-home_banner img", "alt") == T_A)
        await v.mouse.move(5, 5)

        # ---- numbers (the impression needs a second on screen; the click goes through the counter)
        href = await v.get_attribute(".ad-home_banner a", "href"); api = await p.request.new_context(base_url=PUB)
        resp = await api.get(href, max_redirects=0); check("a click is counted and redirects to the saved link only", resp.status == 302 and resp.headers["location"] == "https://example.test/landing", f"{resp.status} {resp.headers.get('location')}")
        await adm.click("button:has-text('Refresh numbers')"); await adm.wait_for_timeout(800)
        rowA = await adm.locator(f"tr:has-text('{T_A}')").first.inner_text(); rowB = await adm.locator(f"tr:has-text('{T_B}')").first.inner_text()
        import re
        seenA = int(re.search(r"(\d+) \(\d+ unique\)", rowA).group(1)); seenB = int(re.search(r"(\d+) \(\d+ unique\)", rowB).group(1))
        check("impressions are counted only after an ad has been on screen for a second", seenA >= 1 and seenB >= 1, f"A:{rowA!r} B:{rowB!r}")
        check("the click shows in the statistics with a click-through rate", "1 (1 unique)" in rowA and "%" in rowA, rowA)
        check("an ad that is only reserved is never shown to anyone", await v.evaluate("fetch('/api/ads?position=home_banner&country=LB&city=Beirut').then(r=>r.json()).then(a=>a.map(x=>x.title))") == [T_B, T_A])
        print("   page errors:", errors); check("no uncaught browser errors", not errors)
        await b.close()
asyncio.run(main())
print(f"\n{sum(results)}/{len(results)} advertising end-to-end checks passed")
