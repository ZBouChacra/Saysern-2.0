"""End-to-end journey 03_uploads: drives the real public site (http://127.0.0.1:5173) and back office (http://127.0.0.1:5174) in a browser, against a running API and an empty database.
See tests/e2e/README.md for how to start everything. Environment: SAYSERNE_MIGRATOR_PROJECT (path to api/Sayserne.Migrator), SAYSERNE_MIGRATOR_CONNECTION (to promote the first admin),
E2E_MYSQL (default "mysql -uroot") and E2E_DB (default "sayserne_dev") for the few steps that move a date back in time."""
import tempfile
import os
import re
import asyncio, base64, hashlib, hmac, os, struct, subprocess, time, uuid
from playwright.async_api import async_playwright, expect

PUB, ADM = "http://127.0.0.1:5173", "http://127.0.0.1:5174"
PW = "correct-horse-battery-9"
results = []
def check(name, cond, extra=""):
    results.append(bool(cond)); print(("PASS " if cond else "FAIL ") + name + (f"   [{extra}]" if (extra and not cond) else ""))
def totp(secret, step):
    key = base64.b32decode(secret + "=" * ((8 - len(secret) % 8) % 8)); h = hmac.new(key, struct.pack(">Q", step), hashlib.sha1).digest(); o = h[-1] & 15
    return f"{(struct.unpack('>I', h[o:o+4])[0] & 0x7fffffff) % 1000000:06d}"
def sh(*args): subprocess.run(args, check=True, capture_output=True, env=os.environ)

async def main():
    uid = uuid.uuid4().hex[:6]; work = f"{tempfile.gettempdir()}/e2e4-{uid}"; os.makedirs(work)
    big, clip, fake = f"{work}/big.jpg", f"{work}/clip.mp4", f"{work}/fake.jpg"
    sh("ffmpeg","-nostdin","-v","error","-y","-f","lavfi","-i","testsrc2=size=3000x2000:rate=1,noise=alls=40:allf=t","-frames:v","1","-q:v","2",big)
    sh("ffmpeg","-nostdin","-v","error","-y","-f","lavfi","-i","testsrc2=size=640x360:rate=25,noise=alls=20:allf=t","-f","lavfi","-i","sine=frequency=440:sample_rate=44100","-t","4","-c:v","libx264","-crf","18","-pix_fmt","yuv420p","-c:a","aac",clip)
    open(fake,"w").write("this is not a picture")
    big_bytes, clip_bytes = os.path.getsize(big), os.path.getsize(clip)
    admin_email, pro_email, cust_email = f"boss{uid}@example.test", f"pia{uid}@example.test", f"cara{uid}@example.test"
    async with async_playwright() as p:
        b = await p.chromium.launch(); errors = []
        async def ui_register(first, email):
            ctx = await b.new_context(); pg = await ctx.new_page(); await pg.goto(PUB + "/register"); await pg.wait_for_selector("text=Create your account")
            await pg.fill('input[autocomplete="given-name"]', first); await pg.fill('input[autocomplete="family-name"]', "Tester"); await pg.fill('input[autocomplete="email"]', email)
            await pg.fill('input[autocomplete="new-password"]', PW); await pg.click("form button.btn"); await pg.wait_for_selector(f"text=Welcome, {first}"); await ctx.close()
        await ui_register("Boss", admin_email); await ui_register("Cara", cust_email)
        subprocess.run(["dotnet","run","--no-build","--project",os.environ.get("SAYSERNE_MIGRATOR_PROJECT", "api/Sayserne.Migrator"),"--","promote-admin",admin_email], env=os.environ, check=True, capture_output=True)

        # a professional, set up through the API, so there is someone to look at and talk to
        api = await p.request.new_context(base_url=PUB, extra_http_headers={"Origin": PUB})
        r = await api.post("/api/auth/register", data={"email": pro_email, "password": PW, "firstName": "Pia", "lastName": "Pro"}); assert r.status == 201, await r.text()
        await api.put("/api/me/profile", data={"firstName": "Pia", "lastName": "Pro", "profileType": "professional", "country": "LB", "city": "Beirut"})
        svc = (await (await api.get("/api/catalog/services")).json())[0]
        pr = await api.post("/api/me/professions", data={"categoryId": svc["categoryId"], "serviceId": svc["id"], "city": "Beirut", "country": "LB", "costPerHour": 50}); assert pr.status == 201
        pro_id = (await (await api.get("/api/auth/me")).json())["user"]["id"]

        # ---------- the professional uploads a profile photo through the page
        pctx = await b.new_context(viewport={"width": 1150, "height": 900}); pia = await pctx.new_page(); pia.on("pageerror", lambda e: errors.append("pia: " + str(e)))
        await pia.goto(PUB + "/login"); await pia.fill('input[type="email"]', pro_email); await pia.fill('input[type="password"]', PW); await pia.click("form button.btn"); await pia.wait_for_selector("text=Welcome, Pia")
        await pia.goto(PUB + "/me"); await pia.wait_for_selector("text=Profile photo")
        await pia.set_input_files('input[aria-label="Profile photo"]', fake); await pia.wait_for_selector(".photo-picker [role=alert]")
        msg = await pia.inner_text(".photo-picker [role=alert]"); check("a file that is not really a picture is refused with a readable reason", "isn't supported" in msg or "not supported" in msg, msg)
        await pia.set_input_files('input[aria-label="Profile photo"]', big); await pia.wait_for_selector(".photo-picker img.avatar.photo", timeout=30000)
        src = await pia.get_attribute(".photo-picker img.avatar.photo", "src")
        info = await pia.evaluate("""async (u) => { const r = await fetch(u); const blob = await r.blob(); const bmp = await createImageBitmap(blob); return {status: r.status, type: r.headers.get('content-type'), bytes: blob.size, w: bmp.width, h: bmp.height}; }""", src)
        check("the uploaded photo is stored as a smaller JPEG (not the original)", info["status"] == 200 and info["type"] == "image/jpeg" and info["bytes"] < big_bytes / 3 and max(info["w"], info["h"]) <= 1600, str(info) + f" original={big_bytes}")
        await pia.click("main button:has-text('Save')"); await pia.wait_for_selector("text=Saved"); await pia.reload(); await pia.wait_for_selector(".photo-picker img.avatar.photo")
        check("the photo is part of the saved profile", (await pia.get_attribute(".photo-picker img.avatar.photo", "src")) == src)

        # ---------- a customer sees it on the professional's page and in search
        cctx = await b.new_context(viewport={"width": 1150, "height": 900}); cara = await cctx.new_page(); cara.on("pageerror", lambda e: errors.append("cara: " + str(e)))
        await cara.goto(PUB + "/login"); await cara.fill('input[type="email"]', cust_email); await cara.fill('input[type="password"]', PW); await cara.click("form button.btn"); await cara.wait_for_selector("text=Welcome, Cara")
        await cara.goto(f"{PUB}/professional/{pro_id}"); await cara.wait_for_selector("img.avatar.photo.big")
        await cara.wait_for_function("(() => { const i = document.querySelector('img.avatar.photo.big'); return i && i.complete && i.naturalWidth > 0; })()", timeout=15000)
        dbg = await cara.evaluate("""async () => { const i = document.querySelector('img.avatar.photo.big'); const r = await fetch(i.src, {credentials: 'same-origin'}); return {src: i.getAttribute('src'), complete: i.complete, w: i.naturalWidth, fetchStatus: r.status, fetchType: r.headers.get('content-type'), loading: i.loading, visible: i.getBoundingClientRect().height}; }""")
        check("the professional's page shows the photo", dbg["complete"] and dbg["w"] > 0, str(dbg))
        await cara.goto(PUB + "/search?country=LB"); await cara.wait_for_selector("a.listing"); await cara.wait_for_timeout(800)
        check("search results show the photo", await cara.locator("a.listing img.avatar.photo").count() >= 1)

        # ---------- chat: a photo and a video
        room = await cara.evaluate("fetch('/api/chat/rooms', {method:'POST', credentials:'same-origin', headers:{'Content-Type':'application/json','X-Requested-With':'sayserne-web'}, body: JSON.stringify({userId: %d})}).then(r => r.json()).then(j => j.id)" % pro_id)
        await cara.goto(f"{PUB}/chats/{room}"); await cara.wait_for_selector('input[aria-label="Attach a file"]')
        accept = await cara.get_attribute('input[aria-label="Attach a file"]', "accept"); check("video is offered while video is switched on", "video/mp4" in accept, accept)
        await cara.set_input_files('input[aria-label="Attach a file"]', big); await cara.wait_for_selector("img.chat-media", timeout=30000)
        check("a photo sent in chat appears in the conversation", await cara.evaluate("document.querySelector('img.chat-media').complete && document.querySelector('img.chat-media').naturalWidth > 0"))
        await cara.set_input_files('input[aria-label="Attach a file"]', clip)
        await cara.wait_for_selector(".bubble.pending", timeout=10000)
        try: await cara.wait_for_selector(".bubble.pending:has-text('Preparing')", timeout=15000); saw_preparing = True
        except Exception: saw_preparing = False
        check("a video shows 'preparing' while the server compresses it", saw_preparing, await cara.inner_text(".thread"))
        await cara.wait_for_selector("video.chat-media", timeout=60000)
        vsrc = await cara.get_attribute("video.chat-media", "src")
        vinfo = await cara.evaluate("""async (u) => { const r = await fetch(u, {headers: {Range: 'bytes=0-99'}}); const full = await fetch(u); const blob = await full.blob(); return {partial: r.status, type: full.headers.get('content-type'), bytes: blob.size}; }""", vsrc)
        check("the compressed video arrives, is MP4 and supports seeking (206)", vinfo["partial"] == 206 and vinfo["type"] == "video/mp4" and vinfo["bytes"] > 0, str(vinfo))
        check("the pending bubble is gone once the video is sent", await cara.locator(".bubble.pending").count() == 0)

        # ---------- the other person sees both, a stranger sees nothing
        await pia.goto(f"{PUB}/chats/{room}"); await pia.wait_for_selector("img.chat-media"); await pia.wait_for_selector("video.chat-media")
        check("the professional sees the photo and the video", await pia.locator("img.chat-media").count() == 1 and await pia.locator("video.chat-media").count() == 1)
        sctx = await b.new_context(); strange = await sctx.new_page(); await strange.goto(PUB + "/login")
        status_anon = await strange.evaluate("fetch(%r).then(r => r.status)" % vsrc); check("anyone not in the conversation gets a 404 for its media", status_anon == 404, str(status_anon))
        await sctx.close()

        # ---------- admin: switch video off; the chat screen follows; the Jobs page shows the work
        actx = await b.new_context(viewport={"width": 1250, "height": 900}); adm = await actx.new_page(); adm.on("pageerror", lambda e: errors.append("admin: " + str(e)))
        await adm.goto(ADM + "/"); await adm.fill('input[type="email"]', admin_email); await adm.fill('input[type="password"]', PW); await adm.click("button:has-text('Continue')")
        await adm.wait_for_selector("[data-testid=totp-secret]"); secret = await adm.inner_text("[data-testid=totp-secret]")
        await adm.fill('input[autocomplete="one-time-code"]', totp(secret, int(time.time() // 30))); await adm.click("button:has-text('Confirm and continue')"); await adm.wait_for_selector("[data-testid=recovery-codes]"); await adm.click("button:has-text('I have saved them')")
        await adm.wait_for_selector("text=Welcome, Boss"); await adm.click("aside >> text=Settings"); await adm.wait_for_selector('[data-setting="Media.VideoEnabled"]')
        row = adm.locator('[data-setting="Media.VideoEnabled"]'); await row.locator("select").select_option("false"); await row.locator("button:has-text('Save')").click(); await expect(row.locator("[role=status]")).to_contain_text("Saved")
        fresh = await b.new_context(); fp = await fresh.new_page(); await fp.goto(PUB + "/login"); await fp.fill('input[type="email"]', cust_email); await fp.fill('input[type="password"]', PW); await fp.click("form button.btn"); await fp.wait_for_selector("text=Welcome, Cara")
        await fp.goto(f"{PUB}/chats/{room}"); await fp.wait_for_selector('input[aria-label="Attach a file"]'); accept2 = await fp.get_attribute('input[aria-label="Attach a file"]', "accept")
        check("switching video off removes it from the chat attach button", "video" not in accept2 and "image/jpeg" in accept2, accept2)
        await fp.set_input_files('input[aria-label="Attach a file"]', clip); await fp.wait_for_selector(".bubble.pending [class*=error], .bubble.pending .error", timeout=15000)
        check("a video sent anyway is refused with the reason", "switched off" in await fp.inner_text(".bubble.pending"), await fp.inner_text(".bubble.pending"))
        await fresh.close()
        await adm.click("aside >> text=Background jobs"); await adm.wait_for_selector("[data-testid=job-counts]"); await adm.wait_for_selector("td code:text('media.process')")
        counts = await adm.inner_text("[data-testid=job-counts]"); check("the Jobs page shows the finished compression", int(re.search(r"done: (\d+)", counts).group(1)) >= 1 and "failed: 0" in counts, counts)
        check("…and lists the job by type", await adm.locator("td code:text('media.process')").count() >= 1)

        print("   page errors:", errors); check("no uncaught browser errors", not errors)
        await b.close()
asyncio.run(main())
print(f"\n{sum(results)}/{len(results)} upload end-to-end checks passed")
