"""End-to-end journey 07_payments: drives the real public site (http://127.0.0.1:5173) and back office (http://127.0.0.1:5174) in a browser, against a running API and an empty database.
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
    uid = uuid.uuid4().hex[:6]; work = f"{tempfile.gettempdir()}/e2e8-{uid}"; os.makedirs(work); boss = f"boss{uid}@example.test"; pic = f"{work}/a.jpg"
    subprocess.run(["ffmpeg","-nostdin","-v","error","-y","-f","lavfi","-i","color=c=0x2a7f62:s=1200x300","-frames:v","1",pic], check=True)
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

        async def new_ad(title):
            await adm.click("aside >> text=Ads"); await adm.wait_for_selector("button:has-text('New ad')"); await adm.click("button:has-text('New ad')"); await adm.wait_for_selector("section[aria-label='Ad form']")
            form = adm.locator("section[aria-label='Ad form']"); await form.locator("label:has-text('Title') input").fill(title); await form.locator("select").first.select_option("home_banner")
            await form.locator('input[aria-label="Ad picture"]').set_input_files(pic); await form.locator("img[alt='Ad preview']").wait_for(timeout=20000); await form.locator("input[type=url]").fill("https://example.test/x")
            await form.locator("button:has-text('Save')").click(); await adm.wait_for_selector(f"td:has-text('{title}')")
        async def reserve(title, frm, to):
            await adm.click("aside >> text=Ads"); await adm.wait_for_selector(f"tr:has-text('{title}')"); await adm.locator(f"tr:has-text('{title}') >> button:has-text('Batches')").click()
            sec = adm.locator(f"section[aria-label='Batches of {title}']"); await sec.wait_for()
            await sec.locator('input[aria-label="From"]').fill(day(frm)); await sec.locator('input[aria-label="To"]').fill(day(to)); await sec.locator("button:has-text('Reserve')").click(); await sec.locator("tbody tr:has-text('pending')").wait_for(); return sec

        T1, T2 = f"Receipt ad {uid}", f"Second ad {uid}"
        await new_ad(T1); sec = await reserve(T1, 0, 2)                                           # 3 days at the default 5.00 = 15.00
        await sec.locator("button:has-text('Record payment')").click(); dlg = adm.locator("section[aria-label='Record payment']"); await dlg.wait_for()
        check("the payment dialog is prefilled and shows the amount", await dlg.locator("input").first.input_value() == T1 and "15.00 USD" in await dlg.inner_text())
        await dlg.locator("label:has-text('Receipt made out to') input").fill("")
        check("a receipt cannot be issued to nobody (the button is disabled)", await dlg.locator("button:has-text('Record payment and issue receipt')").is_disabled())
        await dlg.locator("label:has-text('Receipt made out to') input").fill("Acme Co"); await dlg.locator("textarea").fill("Hamra Street, Beirut\nVAT 99")
        await dlg.locator("select").select_option(label="Bank transfer"); await dlg.locator("label:has-text('Reference') input").fill("TT-77"); await dlg.locator("button:has-text('Record payment and issue receipt')").click()
        await sec.locator("tbody span.pill.ok:has-text('paid')").wait_for()
        check("the paid batch shows its receipt number", await sec.locator("tbody a:has-text('SAY-000001')").count() == 1)

        # ---- payments page and the receipt
        await adm.click("aside >> text=Payments"); await adm.wait_for_url("**/payments"); await adm.wait_for_selector("h1:has-text('Payments')"); await adm.wait_for_selector("a:has-text('SAY-000001')")
        row = await adm.locator("tr:has-text('SAY-000001')").inner_text(); check("the payments page lists the receipt with payer, method and total", "Acme Co" in row and "bank transfer" in row and "15.00" in row and "issued" in row, row)
        check("the summary shows the money received", "15.00 USD" in await adm.inner_text("[data-testid=payments-summary]"))
        await adm.click("a:has-text('SAY-000001')"); await adm.wait_for_selector("[data-testid=receipt]")
        txt = await adm.inner_text("[data-testid=receipt]")
        check("the receipt shows who paid, what for, how and the total", all(x in txt for x in ("SAY-000001", "Acme Co", "Hamra Street", T1, "Bank transfer", "TT-77")) and "15.00 USD" in await adm.inner_text("[data-testid=receipt-total]") and "VOID" not in txt, txt)
        await adm.emulate_media(media="print")
        check("when printed, the menu and buttons are hidden and the receipt remains", not await adm.locator("aside").first.is_visible() and not await adm.locator("button:has-text('Print')").is_visible() and await adm.locator("[data-testid=receipt]").is_visible())
        await adm.emulate_media(media="screen")
        await adm.click("button:has-text('Correct who it is made out to')"); await adm.locator("section[aria-label='Correct receipt'] label:has-text('Made out to') input").fill("Acme Holdings"); await adm.locator("section[aria-label='Correct receipt'] button:has-text('Save')").click()
        await adm.wait_for_function("document.querySelector('[data-testid=receipt]').textContent.includes('Acme Holdings')")
        check("the payer can be corrected but the number and amount stay", "SAY-000001" in await adm.inner_text("[data-testid=receipt]") and "15.00 USD" in await adm.inner_text("[data-testid=receipt-total]"))

        # ---- VAT applies to new receipts only
        await adm.click("aside >> text=Settings"); await adm.wait_for_selector('[data-setting="Billing.VatPercent"]')
        row = adm.locator('[data-setting="Billing.VatPercent"]'); await row.locator("input").fill("10"); await row.locator("button:has-text('Save')").click(); await row.locator("[role=status]").wait_for()
        await new_ad(T2); sec = await reserve(T2, 10, 11)                                          # 2 days = 10.00 + 10% VAT
        await sec.locator("button:has-text('Record payment')").click(); await adm.locator("section[aria-label='Record payment'] button:has-text('Record payment and issue receipt')").click(); await sec.locator("tbody span.pill.ok:has-text('paid')").wait_for()
        await sec.locator("tbody a:has-text('SAY-000002')").click(); await adm.wait_for_selector("[data-testid=receipt]"); txt = await adm.inner_text("[data-testid=receipt]")
        check("a new receipt carries the VAT rate set at that moment", "VAT 10%" in txt and "1.00 USD" in txt and "11.00 USD" in await adm.inner_text("[data-testid=receipt-total]"), txt)
        await adm.goto(ADM + "/payments"); await adm.click("a:has-text('SAY-000001')"); await adm.wait_for_selector("[data-testid=receipt]")
        check("the first receipt is unchanged by the later VAT setting", "VAT 10%" not in await adm.inner_text("[data-testid=receipt]") and "15.00 USD" in await adm.inner_text("[data-testid=receipt-total]"))

        # ---- cancelling a paid booking voids the receipt
        await adm.click("aside >> text=Ads"); await adm.wait_for_selector(f"tr:has-text('{T1}')"); await adm.locator(f"tr:has-text('{T1}') >> button:has-text('Batches')").click()
        sec = adm.locator(f"section[aria-label='Batches of {T1}']"); await sec.locator("tbody tr:has-text('paid') >> button:has-text('Cancel')").click(); await sec.locator("tbody span.pill.bad:has-text('cancelled')").wait_for()
        await adm.goto(ADM + "/payments"); await adm.select_option('select[aria-label="Status"]', "void"); await adm.wait_for_function("document.querySelectorAll('table.t tbody tr').length === 1 && document.querySelector('table.t tbody').textContent.includes('SAY-000001')")
        check("the cancelled booking's receipt is void, its number kept", await adm.locator("tbody tr").count() == 1 and "void" in await adm.locator("tbody tr").first.inner_text())
        summary = await adm.inner_text("[data-testid=payments-summary]"); check("voided money is no longer counted as received", "11.00 USD" in summary and "15.00" not in summary and "void receipts: 1" in summary, summary)
        await adm.click("a:has-text('SAY-000001')"); await adm.wait_for_selector(".void-banner"); check("a void receipt is stamped VOID", "VOID" in await adm.inner_text(".void-banner"))
        print("   page errors:", errors); check("no uncaught browser errors", not errors)
        await b.close()
asyncio.run(main())
print(f"\n{sum(results)}/{len(results)} payments end-to-end checks passed")
