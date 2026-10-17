# Browser journeys

Ten scripts that drive the **real public site and back office in a browser** (Chromium, through Playwright) against a **running API and an empty database**, the way a person would: registering, booking, chatting, uploading, paying, moderating, switching to Arabic, disabling and re-enabling an account, branding the site. 216 checks in total. They complement the .NET tests (which test the API and the database) and the unit tests (which test small pieces of the front end).

| Script | Covers |
|---|---|
| `01_admin.py` | Back-office sign-in with two-factor, users, roles, settings, lookups, texts, catalog, audit (43 checks) |
| `02_marketplace.py` | Search, profile, booking, chat, reviews, alerts (24) |
| `03_uploads.py` | Profile and banner pictures, chat photos and videos, the Jobs page (17) |
| `04_email.py` | Verification, password reset, notifications, e-mail log, test e-mail (22) |
| `05_ads.py` | Ads, batches, prices, the public ad slots and visitor location (20) |
| `06_premium.py` | Premium bookings, ranking, capacity (11) |
| `07_payments.py` | Recording a payment, receipts, voiding (14) |
| `08_moderation.py` | Review answers and hiding, deleting chats, staff reading and writing in a chat (18) |
| `09_contact_dashboard.py` | Contact form and inbox, About/Terms/Privacy, dashboard, year table and CSV, user details (18) |
| `10_phase4.py` | Arabic and right-to-left, saved language, Settings, password change, disable and re-enable by e-mail, branding, the back office in Arabic (29) |

## Running one

1. Install: `pip install playwright pillow && playwright install chromium`.
2. Start a **fresh** database (provision + migrate, as in the main README), the API (`http://127.0.0.1:5080`), the public site on `http://127.0.0.1:5173` and the back office on `http://127.0.0.1:5174`, each forwarding `/api` to the API (`npm run dev` in `web/` and `admin/` does that; `vite preview` works too). To keep the journeys quick use a low password-hashing cost and fast job polling: `Auth__PasswordIterations=20000`, `Settings__RefreshSeconds=2`, `Jobs__PollSeconds=1`, `ASPNETCORE_ENVIRONMENT=Development`.
3. Set the environment the scripts read:
   * `SAYSERNE_MIGRATOR_CONNECTION`: each script registers its own "boss" account and makes it a super admin with `dotnet run --project api/Sayserne.Migrator -- promote-admin`. Set `SAYSERNE_MIGRATOR_PROJECT` to that project's path if you run from another folder.
   * `E2E_MYSQL` (default `mysql -uroot`) and `E2E_DB` (default `sayserne_dev`): a few steps move an appointment or a payment into the past with one SQL statement.
4. `python3 tests/e2e/10_phase4.py`. Each script prints one line per check and a total. The process exit code is not an error code, so look for `FAIL` lines.

Every run registers accounts with fresh random addresses, so scripts can be run repeatedly against the same database. Scripts `04`, `09` and `10` start a small SMTP server on port 2526 to receive the e-mails the site sends and point the site's e-mail settings at it, so **run them against a throw-away database**, never a real one.

**What has and has not been tried.** These scripts were written and run in a sandbox with a custom runner (not included: it builds from a temporary copy of the repository). Two of them (`08`, `09`) have also been run from this folder through that runner. Starting the servers by hand as in step 2 is the same thing, but that exact sequence has not been tried.
