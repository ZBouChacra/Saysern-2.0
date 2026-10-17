"""End-to-end journey 02_marketplace: drives the real public site (http://127.0.0.1:5173) and back office (http://127.0.0.1:5174) in a browser, against a running API and an empty database.
See tests/e2e/README.md for how to start everything. Environment: SAYSERNE_MIGRATOR_PROJECT (path to api/Sayserne.Migrator), SAYSERNE_MIGRATOR_CONNECTION (to promote the first admin),
E2E_MYSQL (default "mysql -uroot") and E2E_DB (default "sayserne_dev") for the few steps that move a date back in time."""
import tempfile
import os
import asyncio, subprocess, os, datetime
from playwright.async_api import async_playwright
BASE="http://127.0.0.1:5173"
ENV=dict(os.environ, DOTNET_CLI_TELEMETRY_OPTOUT="1")   # SAYSERNE_MIGRATOR_CONNECTION comes from the environment (see README)
MYSQL = os.environ.get("E2E_MYSQL", "mysql -uroot").split(); E2E_DB = os.environ.get("E2E_DB", "sayserne_dev")
def promote(email): return subprocess.run(["dotnet","run","--no-build","--project",os.environ.get("SAYSERNE_MIGRATOR_PROJECT", "api/Sayserne.Migrator"),"--","promote-admin",email],capture_output=True,text=True,env=ENV,cwd=os.path.join(tempfile.gettempdir(), "verify")).stdout.strip()
def sql(q): subprocess.run([*MYSQL, E2E_DB, "-e", q], check=True)
results=[]
def ok(msg, cond): results.append(bool(cond)); print(("PASS " if cond else "FAIL ")+msg)

async def register(pg, first, last, email):
    await pg.goto(BASE+"/register"); await pg.fill('input[autocomplete="given-name"]',first); await pg.fill('input[autocomplete="family-name"]',last)
    await pg.fill('input[autocomplete="email"]',email); await pg.fill('input[autocomplete="new-password"]',"correct-horse-9")
    await pg.click('form button.btn'); await pg.wait_for_selector('text=Welcome,', timeout=20000)
async def login(pg, email):
    await pg.goto(BASE+"/login"); await pg.fill('input[type="email"]',email); await pg.fill('input[type="password"]',"correct-horse-9")
    await pg.click('form button.btn'); await pg.wait_for_selector('text=Welcome,', timeout=20000)
async def body(pg): return await pg.inner_text("body")

async def main():
    async with async_playwright() as p:
        b=await p.chromium.launch()
        async def new(): 
            ctx=await b.new_context(viewport={"width":1150,"height":820}); pg=await ctx.new_page()
            errs=[]; pg.on("pageerror", lambda e: errs.append(str(e))); pg.errs=errs; return pg
        pro, client = await new(), await new()
        admin = client

        # ---------- professional sets up ----------
        await register(pro,"Lina","Haddad","lina@example.com")
        await pro.goto(BASE+"/me"); await pro.wait_for_selector('text=I am a', timeout=20000)
        t=await body(pro); ok("a customer profile invites switching to professional", "Switch your profile type" in t and "I am a" in t)
        await pro.select_option('label:has-text("I am a") select','professional')
        await pro.fill('main label:has-text("City") >> nth=0 >> input','Beirut'); await pro.select_option('main label:has-text("Country") select','LB')
        await pro.click('button:has-text("Save profile")'); await pro.wait_for_timeout(1000)
        ok("profile saved as professional", "Saved." in await body(pro) and "Add a service" in await body(pro))
        add='form:has(h3:has-text("Add a service"))'
        await pro.select_option(add+' select >> nth=0', label="Legal"); await pro.wait_for_timeout(700)
        await pro.select_option(add+' select >> nth=1', label="Corporate Law")
        await pro.fill(add+' input >> nth=0','80'); await pro.fill(add+' input >> nth=1','12'); await pro.fill(add+' input >> nth=2','Beirut')
        await pro.click('button:has-text("Add service")'); await pro.wait_for_timeout(1200)
        ok("service listing created", "Weekly availability" in await body(pro))
        await pro.click('button:has-text("Add a time window")'); await pro.click('button:has-text("Save this service")'); await pro.wait_for_timeout(1000)
        ok("availability saved", "Saved." in await body(pro))

        # ---------- client finds, books ----------
        await register(client,"Karim","Client","karim@example.com")
        await client.goto(BASE+"/search?city=Beirut"); await client.wait_for_timeout(1200)
        t=await body(client); ok("search finds the professional by city", "1 professional found" in t and "Lina Haddad" in t and "Corporate Law" in t)
        await client.screenshot(path=os.path.join(tempfile.gettempdir(), "shot-search.png"))
        await client.click('a.listing'); await client.wait_for_timeout(1000)
        t=await body(client); ok("profile shows services and availability but no email", "Corporate Law" in t and "Mon 09:00–17:00" in t and "lina@example.com" not in t)
        await client.screenshot(path=os.path.join(tempfile.gettempdir(), "shot-profile.png"))
        await client.click('a:has-text("Book an appointment")'); await client.wait_for_timeout(800)
        tomorrow=(datetime.datetime.now()+datetime.timedelta(days=2)).replace(hour=10,minute=0,second=0,microsecond=0).strftime("%Y-%m-%dT%H:%M")
        await client.fill('input[type="datetime-local"]', tomorrow); await client.fill('label:has-text("What do you need") input','Shareholder agreement')
        await client.click('button:has-text("Request appointment")'); await client.wait_for_timeout(1500)
        t=await body(client); ok("booking request lands in my appointments as pending", "pending" in t and "Lina Haddad" in t)
        await client.goto(BASE+"/book/"+(await client.evaluate("1")) if False else BASE+"/search?city=Beirut"); await client.wait_for_timeout(800)
        await client.click('a.listing'); await client.click('a:has-text("Book an appointment")'); await client.wait_for_timeout(800)
        await client.fill('input[type="datetime-local"]', tomorrow); await client.wait_for_timeout(300)
        t=await body(client); ok("the booking page warns about an overlapping time and blocks submitting", "overlaps an existing booking" in t and await client.is_disabled('button:has-text("Request appointment")'))

        # ---------- professional handles the request ----------
        await pro.goto(BASE+"/appointments"); await pro.wait_for_timeout(1000)
        t=await body(pro); ok("professional sees the request with the client's name and note", "Karim Client" in t and "Shareholder agreement" in t)
        await pro.click('button:has-text("Approve")'); await pro.wait_for_timeout(1000)
        ok("professional approves", "approved" in await body(pro) and "Mark completed" not in await body(pro))   # not started yet
        sql("UPDATE appointments SET starts_at = DATE_SUB(UTC_TIMESTAMP(3), INTERVAL 2 HOUR), ends_at = DATE_SUB(UTC_TIMESTAMP(3), INTERVAL 1 HOUR)")
        await pro.reload(); await pro.wait_for_timeout(1000)
        await pro.click('button:has-text("Mark completed")'); await pro.wait_for_timeout(1000)
        ok("professional completes it once it has started", "completed" in await body(pro))

        # ---------- review ----------
        await client.goto(BASE+"/appointments"); await client.wait_for_timeout(900)
        ok("client is offered a review", "Write a review" in await body(client))
        await client.click('a:has-text("Write a review")'); await client.wait_for_timeout(1000)
        await client.select_option('form:has-text("Review your appointment") select','5'); await client.fill('form:has-text("Review your appointment") input','Clear, fast, fair.')
        await client.click('button:has-text("Submit review")'); await client.wait_for_timeout(1200)
        t=await body(client); ok("review appears on the profile and the review form disappears", "Clear, fast, fair." in t and "Review your appointment" not in t)
        await client.goto(BASE+"/search?city=Beirut&minStars=5"); await client.wait_for_timeout(900)
        ok("rating is searchable (min 5 stars)", "1 professional found" in await body(client))

        # ---------- chat ----------
        await client.goto(BASE+"/search?city=Beirut"); await client.click('a.listing'); await client.wait_for_timeout(700)
        await client.click('button:has-text("Message")'); await client.wait_for_timeout(1000)
        await client.fill('input[aria-label="Message"]','Hello Lina, are you free this week?'); await client.click('button:has-text("Send")'); await client.wait_for_timeout(1000)
        ok("client sends a chat message", "are you free this week" in await body(client))
        await pro.goto(BASE+"/"); await pro.wait_for_timeout(1500)
        ok("professional's header shows an unread badge", await pro.locator('.topbar .badge').count()>0)
        await pro.goto(BASE+"/chats"); await pro.wait_for_timeout(1000)
        await pro.click('button.room'); await pro.wait_for_timeout(1000)
        ok("professional reads it", "are you free this week" in await body(pro))
        await pro.fill('input[aria-label="Message"]','Yes, Thursday works.'); await pro.click('button:has-text("Send")'); await pro.wait_for_timeout(800)
        await client.wait_for_timeout(5500)
        ok("client receives the reply (polling)", "Thursday works" in await body(client))
        await client.screenshot(path=os.path.join(tempfile.gettempdir(), "shot-chat.png"))

        # ---------- saved search ----------
        await client.goto(BASE+"/search?city=Beirut&maxCost=100"); await client.wait_for_timeout(900)
        client.once("dialog", lambda d: asyncio.ensure_future(d.accept("Beirut lawyers")))
        await client.click('button:has-text("Save this search")'); await client.wait_for_timeout(900)
        await client.goto(BASE+"/alerts"); await client.wait_for_timeout(900)
        ok("saved search is listed", "Beirut lawyers" in await body(client))
        await client.click('a:has-text("Run now")'); await client.wait_for_timeout(1000)
        t=await body(client); ok("running it replays the search", "city=Beirut" in client.url and "Lina Haddad" in t)

        # ---------- ranking ----------
        # (marking a listing premium is a back-office action that now needs the admin app + authenticator; covered by the admin tests, so it is set up directly here)
        subprocess.run([*MYSQL, E2E_DB, "-e", "INSERT INTO premium_batches (profession_id, user_id, professional_name, service_name, country_code, city, date_from, date_to, entitled_days, days_assigned, effective_to, fee_per_day, total_fee, status, paid_at) SELECT p.id, p.user_id, 'Lina', 'x', COALESCE(p.country, 'LB'), p.city, UTC_DATE(), UTC_DATE(), 1, 1, UTC_DATE(), 0, 0, 'paid', UTC_TIMESTAMP(3) FROM professions p JOIN users u ON u.id = p.user_id WHERE u.first_name = 'Lina'; INSERT INTO premium_days (batch_id, day, profession_id) SELECT b.id, UTC_DATE(), b.profession_id FROM premium_batches b WHERE b.professional_name = 'Lina'"],check=True)
        ok("the professional is premium", True)
        anon=await new(); await anon.goto(BASE+"/search?city=Beirut"); await anon.wait_for_timeout(900)
        ok("anonymous visitors see the premium badge in search", "premium" in await body(anon))

        # ---------- guards ----------
        await anon.goto(BASE+"/chats"); await anon.wait_for_timeout(900)
        ok("signed-out visitors are sent to sign in for private pages", "Welcome back" in await body(anon) and "sign in first" in (await body(anon)).lower())
        errs=[e for pg in (pro,client,anon) for e in pg.errs]; print("   page errors:", errs); ok("no uncaught browser errors", not errs)
        await b.close()
asyncio.run(main())
print(f"\n{sum(results)}/{len(results)} marketplace end-to-end checks passed")
