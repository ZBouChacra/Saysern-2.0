# SaySerné — two-tier refactor

A Lebanese professional-services marketplace, refactored into:

| Tier | Technology | Responsibility |
|---|---|---|
| **Web** (`web/`) | TypeScript · React 19 · Vite | The public site (visitors, customers, professionals). Talks to the API over `/api`; never to the database or to Google directly. Contains **no hard-coded lists or limits**: it loads settings, lookups and texts from the API. |
| **Back office** (`admin/`) | TypeScript · React 19 · Vite | A **separate web app** for staff: users, roles, settings, lookups, texts, catalog, audit log. Own build, own host, same API and database. |
| **Application** (`api/Sayserne.Api`) | C# · ASP.NET Core (.NET 10) | API endpoints, business rules, authentication, authorisation, the back-office API. |
| **Database** (`db/`) | MySQL 8 | Tables and **stored procedures**, versioned as migrations. |

```
Visitors ──▶ web/  ──/api──┐
                            ├─▶ ASP.NET Core API ──CALL sp_*()──▶ MySQL 8
Staff    ──▶ admin/ ─/api──┘      │  cookie sessions (separate for staff), CSRF check, rate limits
 (password + authenticator)       └─ Google OIDC (public site only)
```

> **Status: phases 1, 2 and 3a done; 3b in progress: files, media, background jobs and the e-mail system are built (with their screens). Ads, premium listings, fees and payments are next.** Foundation and authentication (1), the core marketplace (2), and the **configuration platform** (3a): runtime settings, lookups, translated texts, audit log, roles and permissions, two-factor back-office sign-in, user/catalog management — in a separate admin app.
> Not built yet: ads, premium listings, fees and payments, file storage, e-mail and notifications (3b); contact messages, review and chat moderation, dashboard, About/Contact/legal content (3c); Arabic and right-to-left screens, theme and Settings page, image uploads in the screens, marketing sections, account disabling (4). The mobile app comes after the web application.

---

## Quick start (local)

**Prerequisites:** .NET 10 SDK, Node 22+, MySQL 8.0+ (not TiDB — it has no stored procedures), Git.

### 1. Create the database and its two accounts (one-off)

`provision` needs a MySQL administrator that may create databases and users. It creates:

* `sayserne_migrator` — owns the schema (runs migrations);
* `sayserne_app` — what the API uses. **It can only `EXECUTE` stored procedures; it has no table privileges.**

PowerShell:

```powershell
$env:SAYSERNE_ADMIN_CONNECTION  = "Server=127.0.0.1;Port=3306;User ID=root;Password=<root password>"
$env:SAYSERNE_DB_NAME           = "sayserne"
$env:SAYSERNE_MIGRATOR_USER     = "sayserne_migrator"
$env:SAYSERNE_MIGRATOR_PASSWORD = "<12-64 chars>"
$env:SAYSERNE_APP_USER          = "sayserne_app"
$env:SAYSERNE_APP_PASSWORD      = "<12-64 chars, different>"
dotnet run --project api/Sayserne.Migrator -- provision
```

bash:

```bash
export SAYSERNE_ADMIN_CONNECTION="Server=127.0.0.1;Port=3306;User ID=root;Password=<root password>"
export SAYSERNE_DB_NAME=sayserne SAYSERNE_MIGRATOR_USER=sayserne_migrator SAYSERNE_APP_USER=sayserne_app
export SAYSERNE_MIGRATOR_PASSWORD='<12-64 chars>' SAYSERNE_APP_PASSWORD='<12-64 chars, different>'
dotnet run --project api/Sayserne.Migrator -- provision
```

Passwords for these two accounts may contain letters, digits and `. _ ~ ! @ # % ^ * + = : , -` (no quotes, backslashes, `$` or `;`). If the API runs on a different machine than MySQL, also set `SAYSERNE_USER_HOST` (e.g. `%`).

### 2. Deploy tables and stored procedures

```powershell
$env:SAYSERNE_MIGRATOR_CONNECTION = "Server=127.0.0.1;Port=3306;Database=sayserne;User ID=sayserne_migrator;Password=<migrator password>"
dotnet run --project api/Sayserne.Migrator -- migrate
```

Safe to run any number of times; run it on every deployment (see [Migrations](#migrations)).

### 3. Run the API

Secrets for local development go in **user-secrets** (stored outside the repo):

```powershell
cd api/Sayserne.Api
dotnet user-secrets set "ConnectionStrings:Default" "Server=127.0.0.1;Port=3306;Database=sayserne;User ID=sayserne_app;Password=<app password>"
dotnet run            # http://localhost:5080
```

### 4. Run the web app

```powershell
cd web
npm install
npm run dev           # http://localhost:5173  (proxies /api to :5080)
```

Open <http://localhost:5173>, register an account, and sign in. A starter catalog (7 categories, 15 services) is created by migration `V004`; it is placeholder data — replace it in the back office (**Catalog**).

To try the marketplace: open **Profile**, set "I am a" to *professional*, add a service, then use a second browser (or private window) as a client to search, book, message and review.

### 5. Run the back office

```powershell
cd admin
npm install
npm run dev           # http://localhost:5174  (proxies /api to :5080)
```

### 6. Make yourself a super admin

There is deliberately no public way to become an admin. Register on the public site, then:

```powershell
dotnet run --project api/Sayserne.Migrator -- promote-admin you@example.com   # needs SAYSERNE_MIGRATOR_CONNECTION
```

Open the **back office** (<http://localhost:5174>), sign in with your email and password, and you are taken through **authenticator setup** (scan the QR code with Google Authenticator, Microsoft Authenticator, Authy or 1Password, then confirm a code). You are shown **10 one-time recovery codes** — store them somewhere safe. Every later sign-in needs password + a code.

If every admin loses their device *and* recovery codes, the break-glass command clears two-factor so the admin can enrol again:

```powershell
dotnet run --project api/Sayserne.Migrator -- reset-2fa you@example.com
```

---

## Google sign-in

1. In [Google Cloud Console](https://console.cloud.google.com/) → *APIs & Services* → **OAuth consent screen**: configure the app (scopes `openid`, `email`, `profile`).
2. **Credentials → Create credentials → OAuth client ID → Web application.**
3. **Authorized redirect URIs** — must match exactly what the API sends:
   * local: `http://localhost:5173/api/auth/google/callback`
   * production: `https://<your-domain>/api/auth/google/callback`
4. Give the API the values (never commit them):

   ```powershell
   cd api/Sayserne.Api
   dotnet user-secrets set "Google:ClientId"     "<client id>"
   dotnet user-secrets set "Google:ClientSecret" "<client secret>"
   dotnet user-secrets set "Google:RedirectUri"  "http://localhost:5173/api/auth/google/callback"
   ```
   In production use the environment variables `Google__ClientId`, `Google__ClientSecret`, `Google__RedirectUri`.

Until `ClientId` and `ClientSecret` are set, `/api/auth/providers` reports `google: false`, the web app hides the Google button, and `/api/auth/google/start` returns 503.

### How the flow works (all validation is server-side)

1. `GET /api/auth/google/start` creates a random **state**, **nonce** and **PKCE** verifier, stores them in a short-lived encrypted `HttpOnly` cookie (ASP.NET Data Protection), and redirects to Google.
2. Google redirects back to `/api/auth/google/callback?code=…&state=…`. The API checks `state` against the cookie (constant-time; the cookie is single-use).
3. The API exchanges the code **server-to-server** (with the client secret and PKCE verifier) and receives an ID token.
4. The ID token is validated on the server: **RS256 only**, signature against Google's published keys (JWKS, cached, refreshed on unknown `kid`), `iss`, `aud` = your client ID, `exp`, and `nonce`. Nothing the browser sends is trusted for identity.
5. The result is mapped to an account using the rules below.

### Linking Google and password accounts

| Situation | Result |
|---|---|
| Google identity already linked | Sign in to that account. |
| New identity, email not used by any account | Create a new passwordless account (Google must report the email as **verified**). |
| New identity, **email already has an account** | **No automatic merge.** The user is sent to `/link-google` and must enter that account's **password** to prove ownership. Wrong guesses count toward lockout. On success the identity is linked and the account's email becomes verified. |
| Signed-in user clicks *Link a Google account* | The verified Google identity is attached to the current account (`?mode=link`). |
| Google identity already belongs to another user | Refused (`google=already_linked`). |

---

## Database access: stored procedures only

**Rule:** application code contains no SQL text and no direct table access. Every runtime read/write is `CALL sp_…(@params)`.

Enforced in four independent layers:

1. **One gateway.** `api/Sayserne.Api/Data/StoredProcedureExecutor.cs` is the only class that creates a database command, always with `CommandType.StoredProcedure`, parameters as `MySqlParameter`s, and procedure names validated against `^sp_[a-z0-9_]+$`. Repositories (`Data/Repositories.cs`) just map method calls to procedure calls.
2. **No ORM.** The only data dependency is the `MySqlConnector` driver. No EF Core, Dapper or query builder is referenced.
3. **Database privileges.** The API's MySQL account holds `EXECUTE` and nothing else. Even a bug or injection that tried `SELECT * FROM users` is refused by MySQL (`SELECT command denied`). Tests prove it for SELECT, INSERT, UPDATE and DROP.
4. **A test that fails the build** (`tests/Sayserne.Tests/SqlGuardTests.cs`). It scans `api/` for SQL text (string-literal-aware, so SQL can't hide behind a fake comment), forbidden APIs (`FromSql*`, `DbContext`, `CommandText`, `CommandType.Text`, …), raw `MySqlCommand`/`MySqlConnection` use outside the executor, ORM package references, and checks that every procedure name referenced from C# is actually defined in `db/`. It also tests itself against known-bad samples so it cannot silently go blind.

**Where SQL does exist** (reported explicitly, as required):

| Location | What | Why it's allowed |
|---|---|---|
| `db/**/*.sql` | Table DDL, stored-procedure definitions, provisioning scripts | This is the one place for SQL: migrations. |
| `api/Sayserne.Migrator/ScriptRunner.cs` | Executes the text of those `.sql` files (`CommandType.Text`) | Migration/provisioning only. It authors no SQL: it loads and runs files from `db/`. The migration journal itself is read/written through procedures. |
| `tests/Sayserne.Tests/fixtures/*.sql` | Deliberately illegal statements (`SELECT * FROM users`, …) | Run as the app account to *prove* MySQL refuses them. |
| `tests/**/*.cs` | SQL-looking strings used purely as **data** (SQL-injection payloads such as `x'); DROP TABLE users; --`, and inputs for the script splitter) | Test data, not queries. The guard still forbids ORM/direct-query APIs in tests. |
| `MySqlConnector` driver internals | In stored-procedure mode the driver reads `information_schema.routines/parameters` to bind parameters | Driver implementation detail, not application SQL; works under the `EXECUTE`-only account (verified by the integration tests). |

**Application code (`api/Sayserne.Api`) contains zero SQL text** — verified by the guard test.

### Migrations

`db/` holds everything the database needs:

```
db/bootstrap/B000__migration_journal.sql   journal table + its 4 procedures (idempotent, runs every time)
db/migrations/V001__users_and_identities.sql   versioned: applied once, in numeric order
db/migrations/V002__profile_fields.sql     profile columns on users
db/migrations/V003__marketplace_tables.sql categories, services, professions, availability, appointments, reviews, chat, alerts
db/migrations/V004__seed_catalog.sql       placeholder starter catalog
db/migrations/R__000_views.sql             repeatable: the listing view (runs before the procedures)
db/migrations/R__sp_*.sql                  repeatable: stored procedures, re-applied whenever the file changes
                                           (users, identities, admin_users, catalog, professionals, booking, messaging)
db/setup/provision.sql / deprovision.sql   database + the two accounts (templated, validated)
```

* **`V###__name.sql`** — schema changes. Applied exactly once, in order. Editing one after it was applied is **detected and rejected** (checksum, line-ending-insensitive). Add a new `V###` instead.
* **`R__name.sql`** — stored procedures. Each starts with `DROP PROCEDURE IF EXISTS` then `CREATE PROCEDURE`. Change the file, run `migrate`, and the new definition is deployed. This is how procedures reach every environment.
* A MySQL advisory lock stops two migrators running concurrently. DDL in MySQL auto-commits, so keep each `V` script small and ideally re-runnable.
* Procedures signal business errors with `SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'CODE'`; the executor turns that into an `AppException` with an HTTP status (`Infrastructure/AppException.cs`).

**Adding a feature** = (1) a `V###` for tables, (2) an `R__sp_*.sql` for its procedures, (3) a repository method that calls the procedure by name, (4) an endpoint. The guard test fails if step 3 names a procedure that step 2 doesn't define.

---

## Authentication and authorisation

**Public site (customers and professionals)**

* **Registration** (`POST /api/auth/register`): validated server-side. The password rules (minimum length, letter, digit) are **runtime settings** (`Auth.PasswordMinLength` ≥ 8, `Auth.PasswordRequireLetter`, `Auth.PasswordRequireDigit`) and apply to form accounts only — Google sign-in is unaffected. Passwords are hashed with **PBKDF2-HMAC-SHA256** (600 000 iterations — a deploy-time setting — random 128-bit salt, constant-time compare). Duplicate email → `409`.
* **Login:** same error for unknown email and wrong password, with a dummy hash verified for unknown emails to even out timing. After `Auth.MaxFailedAttempts` failures the account is locked for `Auth.LockoutMinutes` (counted in the database).
* **Session:** ASP.NET Core cookie — `HttpOnly`, `SameSite=Lax`, `Secure` outside Development, **fixed length** `Auth.SessionHours` (not sliding). On **every request** the account is re-read: locking a user takes effect immediately.
* **CSRF:** state-changing `/api` requests must come from an allowed origin (same origin, `Web:AllowedOrigins` or `Admin:AllowedOrigins`), or, if the browser sent no `Origin`, carry the `X-Requested-With` header that cross-site forms cannot set.
* **Rate limiting:** auth endpoints per client IP (`Auth.AuthRequestsPerMinute`), writes per user (`Auth.WritesPerMinute`). Changing the setting applies to new windows.

**Back office (staff)** — a different door, on purpose

* **Separate session.** The admin cookie (`sayserne.admin`, `HttpOnly`, **`SameSite=Strict`**) only exists after **password + second factor**. A website session is never accepted by `/api/admin/*`, and an admin session is never accepted by the public API. A super admin who signs in to the *website* gets no back-office powers there.
* **Two-factor (TOTP, RFC 6238):** works with any authenticator app. First sign-in forces enrolment; the secret is stored encrypted (ASP.NET Data Protection); a code can be used **once** (replay is refused by the database, even under a race); 10 single-use **recovery codes** (only hashes stored) can be regenerated with a current code. An enrolled account cannot be re-enrolled by someone who only has the password.
* **Stricter limits:** `Auth.AdminMaxFailedAttempts` (3) / `Auth.AdminLockoutMinutes` (30), idle timeout `Auth.AdminIdleMinutes` (30; activity keeps the session alive) and an absolute `Auth.AdminMaxSessionHours` (12). Customers, role-less users and Google-only accounts get exactly the same answer as a wrong password.
* **Permissions, not a single "admin" flag.** Each route names one permission (`users.read`, `users.manage`, `admins.manage`, `roles.manage`, `settings.read`/`manage`, `lookups.manage`, `translations.manage`, `catalog.manage`, `audit.read`). **Roles are data**: create them and assign them in the back office. A **super admin** holds every permission. Permissions are re-read on **every request**, so removing one (or the role, or the account) takes effect at once.
* **Safety rails:** nobody can lock, demote, re-role or reset the two-factor of themselves; the **last active super admin** can never be locked or demoted (enforced inside the stored procedures, under a row lock); another super admin can reset a colleague's two-factor (lost device).
* **Audit log:** every back-office change (who, what, when, old → new where relevant) is written **in the same database transaction as the change**, so a change cannot happen without its record. The API's database account can neither read nor edit nor delete the table directly, and no endpoint changes entries.
* **Open redirects:** post-login return URLs must be local paths. Responses never include password hashes.

### API summary

Authentication (public site):

| Method & path | Access | Notes |
|---|---|---|
| `GET /api/health` | public | Calls `sp_health_ping`. |
| `GET /api/auth/providers` | public | `{ google, passwordRegistration }` |
| `POST /api/auth/register` · `login` · `logout` | public / session | |
| `GET /api/auth/me` | session | Fresh user from the database. |
| `GET /api/auth/google/start` | public (`?mode=link` needs a session) | Redirect to Google. |
| `GET /api/auth/google/callback` | public | OAuth redirect target. |
| `POST /api/auth/google/link-pending` | pending-link cookie + password | |
| `GET /api/config/public` | public | Settings flagged public, typed (password rules, limits…). Cached 10 s. |
| `GET /api/lookups/{type}?parent=` | public | Active values with a name per language (e.g. `country`, `city?parent=LB`, `nationality`, `weekday`). Cached 60 s. |
| `GET /api/translations/{language}` | public | UI texts, falling back to the default language. Cached 60 s. |

Back office (phase 3a). **Separate session** (password + authenticator); each route needs the permission shown:

| Method & path | Permission | Notes |
|---|---|---|
| `POST /api/admin/auth/login` → `/2fa/enroll` → `/2fa/confirm` · `/verify` · `/logout`, `GET /me`, `POST /recovery-codes/regenerate` | none → staff session | Sign-in steps; `verify` takes an authenticator `code` or a one-time `recoveryCode`. |
| `GET /api/admin/users?search&page&pageSize` | `users.read` | With role names and whether two-factor is on. |
| `POST /api/admin/users/{id}/lock` · `/flags` | `users.manage` | `/flags` sets premium / starred (search ranking). |
| `POST /api/admin/users/{id}/role` · `/two-factor/reset` | `admins.manage` | Super-admin flag; lost-device reset. |
| `PUT /api/admin/users/{id}/roles` | `roles.manage` | Replace the user's roles. |
| `GET·POST /api/admin/roles`, `PUT·DELETE /api/admin/roles/{id}`, `GET /api/admin/permissions` | `roles.manage` | Roles are data; permissions are fixed by the code. |
| `GET /api/admin/settings` · `PUT /{key}` · `POST /{key}/reset` | `settings.read` · `settings.manage` | Validated against type, limits and related settings. |
| `GET /api/admin/lookups/types`, `PUT …/types/{code}`, `GET …/{type}/values`, `PUT …/{type}/values/{code}` | `lookups.manage` | Create/edit values and their translations; deactivate, never delete. |
| `GET /api/admin/translations`, `PUT …/{language}/{key}`, `POST …/{language}/{key}/reset` | `translations.manage` | |
| `GET /api/admin/catalog/categories` · `/services`, `POST·PUT` both | `catalog.manage` | Includes blocked entries. |
| `GET /api/admin/audit?from&to&actorId&action&entityType&search&page` | `audit.read` | Read-only. |

Marketplace (phase 2). Public unless noted; every write is rate-limited per user (setting `Auth.WritesPerMinute`):

| Method & path | Access | Notes |
|---|---|---|
| `GET /api/catalog/categories` · `/services?categoryId=` | public | Active entries only. |
| `GET /api/professionals/top?limit=` | public | |
| `GET /api/professionals/search?…` | public | Filters: `categoryId serviceId firstName lastName sex minAge maxAge nationality country city hasOffice minStars minCost maxCost minExperience maxExperience hasTeam page pageSize`. Ranked premium → starred → rating → experience. |
| `GET /api/professionals/{userId}` | public | Public profile + listings + availability. No email, phone or date of birth (age only). |
| `GET /api/professionals/{userId}/busy` · `/reviews` | public | Busy times only (never who booked or why); reviews. |
| `GET·PUT /api/me/profile` | session | Full private profile; PUT replaces the editable fields. |
| `GET·POST /api/me/professions`, `PUT·DELETE /api/me/professions/{id}` | session | Own service listings. |
| `GET·PUT /api/me/availability` | session | Weekly windows per listing (advisory). |
| `POST /api/appointments`, `GET /api/appointments`, `POST /api/appointments/{id}/status` | session | Booking and the state machine below. |
| `GET /api/reviews/eligible?professionalId=`, `POST /api/reviews` | session | Review a completed appointment, once. |
| `GET·POST /api/chat/rooms`, `GET·POST /api/chat/rooms/{id}/messages`, `GET /api/chat/unread-count` | session | Text and location messages; membership enforced. |
| `GET·POST /api/alerts`, `DELETE /api/alerts/{id}`, `GET /api/alerts/{id}/run` | session | Saved searches (limit: `Alerts.MaxPerUser`). |

**Booking state machine** (enforced inside the stored procedure `sp_appointments_transition`):
`pending → approved` (professional only) · `pending/approved → cancelled` (either party) · `approved → completed` (professional only, and only after the start time). `cancelled` and `completed` are final. Slots are serialised per professional with a row lock, so two clients can never take the same time; back-to-back bookings are allowed. All times are UTC.

---

## The configuration platform (phase 3a)

Nothing a business owner may want to change is compiled in. There are three layers:

1. **Environment** — secrets and infrastructure only (connection strings, Google keys, key folder, allowed origins).
2. **Runtime settings** (table `app_settings`, back office → **Settings**): typed, with a default and **limits that admins cannot exceed** (security-sensitive ones have a floor: the minimum password length cannot go below 8). Edited values are cached in memory per API instance; an edit applies immediately on the instance that handled it and within `Settings:RefreshSeconds` (15 s) on the others. Stored procedures read the same table, so rules such as the booking-length window are enforced in the database too. Cross-checks keep related settings coherent (minimum ≤ maximum). Current settings: password rules, lockout and session lengths (customer and admin), rate limits, booking length and note limits, chat message length, saved-search limit, search page sizes, review/profile text limits, site name and default language. A setting flagged *public* is also served to the web app at `GET /api/config/public` (e.g. so the registration form shows the real password rules).
3. **Lookups and texts** (back office → **Lookups**, **Texts**): countries (171) → cities (524), nationalities (74), sex, languages, saved-search frequencies, currencies, appointment lengths, weekdays, appointment statuses, message and profile types — each value has a **name per language**, an order, an active flag (values are **switched off, never deleted**, so old data keeps its meaning) and free-form metadata. UI texts (currently the error messages) are stored per language with the shipped wording kept so an edit can be reset; a language with no text for a key falls back to the default language. The web app loads all of it from `GET /api/lookups/{type}` and `GET /api/translations/{language}`.

What deliberately stays in code: security rules (who may approve a booking, the booking state machine, password hashing) — making those configurable would let a mistaken setting break security. Status *labels* and colours are data; the allowed *transitions* are not.

**Caching:** browsers may keep `config/public` for 10 s and lookups/texts for 60 s, so a visitor sees an admin's change within about a minute at most.

**Seed data.** `V005`–`V008` create and fill the platform tables. The geography and texts are generated by `python3 db/tools/gen_seed.py` from `db/tools/original_lookups.json` (the original app's hard-coded lists) and `db/tools/messages_en.json`. English names come from the original; **ISO codes and Arabic names are drafts written for this project — please review them in the back office.** The starter roles *Support*, *Operations* and *Content* are suggestions; edit or delete them.

**Stored values are codes.** Country, nationality, sex, language and saved-search frequency are stored as lookup **codes** (`LB`, `lebanese`, `male`, `ar`, `daily`) and validated against the active lookup values by the stored procedures; cities stay free text with suggestions from the lookup.

## Files, media and background jobs (phase 3b, part 1)

**Requirement:** `ffmpeg` and `ffprobe` must be installed on the server (free; Windows, Linux and macOS builds exist). Images and videos are processed with them. There are no extra .NET packages.

* **Uploads** — `POST /api/files?purpose=profile_photo|banner_photo|chat_media[&roomId=]` (multipart, field `file`), `GET /api/files/{id}` (public for profile and banner photos; chat media only for the two people in the conversation; anything else is a plain 404), `GET /api/files/{id}/status` (uploader only). The type is decided from the file's **first bytes**, never its name: only JPEG, PNG, GIF, WebP images and MP4/MOV/WebM video pass. SVG, HTML, PDF, archives, executables and text are refused.
* **Images** become a metadata-free JPEG no larger than `Media.ImageMaxDimension` pixels and `Media.MaxImageMB`; they are never enlarged. A file that claims an absurd size (a "decompression bomb") is refused before decoding. Only plain raster formats are ever decoded (ffmpeg can open SVG and others, which may reference local files; the processor refuses them by an explicit allow-list).
* **Videos** are accepted at once (`202`), then compressed in the background to H.264/AAC MP4, at most `Media.VideoMaxHeight` high, `Media.VideoMaxSeconds` long and `Media.MaxVideoMB` big, metadata removed, fast-start so players can seek. `Media.VideoEnabled` switches video off to save space. Uploads over `Media.MaxUploadMB` are refused (the rest of the upload is read and discarded so the browser gets a clean "too large" answer).
* **Storage** — `Storage.Provider` (runtime setting) chooses where **new** files go: `disk` (this server, `Storage:DiskPath`) or `s3` (any S3-compatible object store, configured through `Storage:S3:*` environment variables). Every file records its own provider, so switching later — for example when the business grows and you buy a storage server — never strands old files. The S3 client is implemented here (AWS Signature V4, verified against Amazon's published test vectors); objects are buffered in memory, which is fine at these sizes.
* **Chat** — messages can be `image` or `video`, referencing the sender's own finished file for that conversation, with an optional caption. A profile or banner photo may be `/api/files/<id>` of the user's own finished upload of that purpose (or still an external link).
* **Background jobs** — table `jobs`, claimed with `FOR UPDATE SKIP LOCKED`, so any number of API instances can run workers and each job runs once. A worker that dies loses its lease (`Jobs.LeaseSeconds`) and another takes over; failures are retried with doubling delays (`Jobs.RetryDelaySeconds`, `Jobs.MaxAttempts`); a permanent failure (an unacceptable file) is not retried. `Jobs:Enabled=false` turns the in-process worker off (run it elsewhere or in tests). Staff with `jobs.read` / `jobs.manage` can list and retry jobs (`/api/admin/jobs`).
* **Known limitations** — transparent PNGs are flattened to JPEG (transparency is lost); a *refused* video cannot be retried (its raw upload is deleted: the user uploads another); S3 downloads are buffered in memory.

## E-mail (phase 3b, part 2)

Nothing about e-mail is compiled in. In the back office (**Settings -> Email**) you choose the provider, the mail server host, port, security, login and **password (stored encrypted, never shown again)**, the sender address and name, and the public address of the website (for links). Use **Send a test email** to check the settings; a wrong password or host is reported at once with the mail server's own words.

* **Off by default.** While `Email.Provider` is `off`, nothing is sent and nothing is demanded (see the verification rule below), so a site without e-mail is never locked out. Switch it to `smtp` once the server details are in.
* **Which e-mails.** Confirm your address, password reset, a new appointment request, approval, cancellation, completion, a new message, a new review and (reserved) saved-search updates. Every text, in English and Arabic, is in **Texts** (`email.<event>.subject` / `.body`, plus `email.footer`, `email.signoff`), with `{{placeholders}}` such as `{{name}}`, `{{link}}`, `{{when}}`; the recipient's preferred language is used. Edits apply to the next e-mail. Arabic e-mails are laid out right-to-left.
* **Delivery.** Every e-mail goes through an outbox and the job queue, so a slow or broken mail server never slows or breaks a booking. A refused message is retried with doubling delays, then marked *failed*; once the mail server is fixed an admin can retry it from **Background jobs**. The text of an e-mail is deleted as soon as it is sent, and the **Email log** shows what happened but never the content.
* **"New message" e-mails** wait `Email.MessageDelayMinutes` and are sent only if the recipient still has unread messages in that conversation: one e-mail per conversation per window, and never the message itself.
* **Verification.** Form-registered users get a link (valid `Email.VerifyTokenHours`). While e-mail is **on** and `Auth.RequireVerifiedEmail` is on, unverified users cannot book or message (`EMAIL_NOT_VERIFIED`); Google sign-in counts as verified. Unverified addresses only ever receive the mandatory e-mails, so nobody can be mail-bombed by someone registering with their address.
* **Password reset.** "Forgot your password?" always gives the same answer whether or not the address has an account. The link works once and expires after `Email.ResetTokenMinutes`; the password rules apply; **every session opened with the old password ends**; proving mailbox control also verifies the address and clears a temporary lockout (an admin lock is never lifted). Tokens are random, stored only as a SHA-256 hash, and limited to `Email.MaxTokenEmailsPerHour` per account. The link opens a page that *posts* the token, so a mail scanner opening it cannot verify or reset anything.
* **Notification choices.** Users choose per event and per channel (**Settings** page). Channels are a lookup: *SMS* and *WhatsApp* exist but are switched off, so they are not offered until they are integrated; mandatory events cannot be switched off.
* **Secret settings** (the SMTP password) are encrypted with ASP.NET Data Protection before they are stored and are kept out of the audit log. **If the Data Protection keys are lost the password must be entered again** (the setting then reports it cannot be decrypted).
* **Limits to know about:** uses .NET's built-in SMTP client, so it supports plain and STARTTLS (port 587) but not implicit TLS on port 465 (use 587). Times in e-mails are shown in UTC until per-user time zones arrive. SPF/DKIM must be set up on your domain by your mail provider for good delivery.

## Saved-search digests and advertising (phase 3b, part 3)

**Digests.** A saved search is emailed on its schedule with only the professionals who joined since the last digest (none new = no email, and the search still moves forward). How long each frequency is lives in the *Alert frequencies* lookup (`metadata.days`), so an admin can add "every 3 days" without a deployment. A small scheduler inside the API (`Jobs:SchedulerEnabled`, every `Jobs:SchedulerMinutes`) queues one job per search per period; the usual rules apply (the user's notification choice, verified address). `Alerts.DigestMaxListings` caps the list; the count in the email is always the true number.

**Advertising (API and database; admin screens and the public slots come next).**
* **Positions** (lookup, admin-extendable): home banner, search banner, sidebar. Ads are created by staff only (`ads.manage`); the picture is uploaded through the back office (`POST /api/admin/ads/images`), shrunk and checked like every upload, and stored as a public `ad_image`. Links must be `http(s)`.
* **Place.** An ad is worldwide, for a country, or for a city. A visitor's place (their profile, or the country/city picker) decides what they are offered: worldwide ads suit everyone, country ads that country, city ads that city, most specific first; with no known place only worldwide ads are shown.
* **Batches.** An ad runs for calendar dates (from/to inclusive, at most a year, not in the past); several batches per ad leave gaps. The **daily fee is fixed when the batch is created** (a later price change never alters it); total = days x fee. A batch is `pending` (reserved), then `paid` (running; marked by staff until payments arrive), or `cancelled` (final). Only paid batches covering today (UTC) are shown.
* **Prices and limits** (`Advertising -> Prices`): a fee and a limit of ads per day, per position and place, resolved city -> country -> the position's worldwide row -> the defaults in Settings, **each value on its own**. A per-ad fee overrides the price.
* **Hard stop.** A batch is refused (`AD_CAPACITY_EXCEEDED`) if any of its days would exceed the limit for that position among ads whose places overlap (a worldwide ad overlaps everything, a country ad its cities, two different cities never overlap). Reserved and paid batches count; cancelling or switching an ad off frees the days. Bookings are serialised per position, so two admins cannot overbook at the same moment. A preview endpoint shows booked/limit per day.
* **Tracking.** The page reports an impression (`POST /api/ads/{id}/impression`, sent by the page after the ad has been visible for a second) and clicks go through `GET /api/ads/{id}/click`, which counts and redirects to the saved link only. Totals and "unique" (once per visitor per day) are kept per ad per day. A visitor is an anonymous random cookie of which **only a hash is stored: no IP address, no account**. Raw events are deleted after `Ads.EventRetentionDays` by a daily housekeeping job; the daily totals are kept. Reports are rate limited per address (`Ads.EventsPerMinute`); showing ads is not.
* **Known limits:** dates are UTC calendar days; switching a switched-off ad back on does not re-check capacity; rotation is done by the page (`Ads.RotationSeconds`, public setting).

## Configuration reference

Deploy-time configuration only — **business rules are runtime settings in the back office** (see above). Values can be environment variables (`Section__Key`), user-secrets, or `appsettings.*.json` — but **secrets only via environment variables or user-secrets**. See `.env.example` for the full list.

| Key | Purpose | Default |
|---|---|---|
| `ConnectionStrings:Default` | API → MySQL (the `EXECUTE`-only account). **Required**; the API refuses to start without it. | — |
| `Google:ClientId` / `ClientSecret` / `RedirectUri` | Google sign-in | empty (feature off) |
| `Web:AllowedOrigins` | Extra origins allowed to POST (CSRF) | `http://localhost:5173` |
| `Admin:AllowedOrigins` | The back-office app's origin(s) | `http://localhost:5174` |
| `Auth:PasswordIterations` | PBKDF2 cost (deploy-time: it must match the hardware) | 600000 |
| `Settings:RefreshSeconds` | How often each API instance re-reads the runtime settings | 15 |
| `Proxy:TrustForwardedHeaders` | Honour `X-Forwarded-For/Proto` | `false` |
| `DataProtection:KeysPath` | Folder for encryption keys. Set it in production and share it across instances (framework default is per-machine and can be ephemeral in containers) | framework default |

### Production notes

* Serve the public site, the back office and the API as: `https://<domain>` (web + `/api`) and `https://<admin host>` (admin app + `/api`), both reverse-proxying `/api` to the same API, so cookies stay first-party. Put the back office on its own host name and, if you can, restrict it (IP allow-list or VPN) — it is public-capable by design (strong sign-in) but need not be public. The Google redirect URI is then `https://<domain>/api/auth/google/callback` and cookies stay first-party.
* Run with `ASPNETCORE_ENVIRONMENT=Production` (cookies become `Secure`; HTTPS required). If TLS terminates at a proxy, set `Proxy__TrustForwardedHeaders=true` **only** when the API is reachable exclusively through that proxy.
* **Persist and protect the Data Protection key folder** (`DataProtection__KeysPath`) and share it across instances. It also encrypts the admins' two-factor secrets: if the keys are lost, every admin must re-enrol (another super admin can reset them, or use the `reset-2fa` command).
* MySQL `max_connections` must exceed the sum of the API instances' pools; the test suite needs a few dozen.
* Run `migrate` as part of each deployment, before starting the new API version.

---

## Tests and checks

```powershell
# API + database + migrations + Google flow + SQL guard (needs MySQL; creates and removes sayserne_test_* databases)
$env:SAYSERNE_TEST_ADMIN_CONNECTION = "Server=127.0.0.1;Port=3306;User ID=root;Password=<root password>"
dotnet test Sayserne.sln

# Web
cd web
npm run build      # type-check + production build
npm test           # api client + validation unit tests

# Back office
cd ../admin
npm run build
npm test
```

What the .NET suite covers: password hashing; input validation; Google ID-token validation (bad signature, wrong audience/issuer, expiry, nonce, `alg: none`, unknown key); the complete Google flow against an in-process fake Google that enforces the client secret and PKCE (new user, returning user, state mismatch, replay, unverified email, account linking with and without the password, explicit linking, locked accounts); registration/login/lockout/logout; CSRF and rate limiting; admin authorisation (anonymous 401, user 403, immediate effect of lock/role changes, last-admin protection); migrations (idempotent, tamper detection, repeatable redeploy, naming rules); the app account being refused direct table access; SQL-injection payloads treated as data; the architecture guard; **the configuration platform** — settings (types, limits, floors, consistency, public exposure, effect on password rules / lockout / rate limits / booking / chat / alerts / paging, propagation to a second instance, audit with old and new value), lookups (seed data, hierarchy, admin edits, deactivation, validation of profile values against them, default language protected), translations (languages, fallback, edit/reset), roles and permissions (exact access per role, immediate effect of changes, in-use roles protected), **two-factor sign-in** (password alone is no session, enrolment, replay refused — also at the database level under a 20-way race, recovery codes single use, lockout, forged pending cookie ignored, idle and absolute timeouts, cookie flags, separation of admin and website sessions, break-glass reset), the audit log (every change recorded, filters, append-only), and catalog management. Security-critical rules were mutation-tested: breaking each one makes a specific test fail.

CI (`.github/workflows/ci.yml`) runs both suites against a MySQL 8 service container.

---

## Roadmap

| Phase | Scope | Status |
|---|---|---|
| 1 | Database foundation, migrations, auth (form + Google), admin users, web shell | **done** |
| 2 | Catalog, professional profiles/listings, availability, search, booking, reviews, chat, saved searches (+ web pages) | **done** |
| 3a | Configuration platform: runtime settings, lookups, translated texts, audit log, roles & permissions, two-factor back-office sign-in, **separate admin app**, users, catalog; public web reads everything from the API | **done** |
| 3b | **Done:** file storage (object storage ⇄ server disk, switchable), image/video processing and their screens, background jobs, **e-mail system** (provider settings, templates, verification, password reset, notifications and preferences). **Still to do:** saved-search digests; **ads** (3 positions, calendar-date batches, daily fees, impressions/clicks); **premium listings** (calendar batches, per-location limits, country-scoped ranking); fees and manual payments with receipts | in progress (digests and the advertising API are done; ad screens, premium listings, fees and payments remain) |
| 3c | Contact messages, review moderation (professional replies, admin soft-delete) and chat monitoring (incl. deleted chats, highlighted admin interventions), dashboard (current year + year filter, CSV export), About / Contact / legal content, branding (logo, name, palettes) | not started |
| 4 | Arabic + right-to-left screens, theme toggle and Settings page, image/video uploads in the screens, marketing sections, account disabling/re-enabling, per-user time zones and availability enforcement, final parity check, OpenAPI client | not started |

## Differences from the original app (phase 2)

Reading the original code surfaced several security problems. They were **not** ported; each has a regression test.

| Original behaviour | Now |
|---|---|
| Anyone could read or write **any chat room** by guessing its id | Membership is checked in the stored procedure; outsiders get "not found" and cannot mark messages read. |
| Either party could set an appointment to **approved or completed** (a client could approve their own booking and complete it to unlock a review) | State machine above; clients can only cancel. |
| A review only required *some* completed booking with that professional, and the professional came from the request | Only the appointment's own client, only after completion, once per appointment; the professional is taken from the appointment. |
| Any signed-in user could **delete anyone's alert** | Owner-only. |
| The public profile and search results exposed **email, phone and date of birth** | Public data excludes them (age only). |
| A public endpoint returned full appointment rows (client ids, notes) | `busy` returns times only. |
| Booking checked overlaps without locking, and treated back-to-back slots as clashes | Row-locked (an 80-way simultaneous-booking test yields exactly one booking; it yields double bookings without the lock); back-to-back allowed. |
| Bookings could be made in the past, with any duration, for yourself, or for non-professionals | Rejected. |
| Search treated `%` and `_` in text as wildcards | Escaped; searched literally. |

Other deliberate changes: a service may be listed once per professional; owners **hide** a listing (`is_hidden`) while administrators **lock** it (`is_locked`), so an owner cannot undo an admin lock; a professional with live listings must hide them before switching back to a customer profile; profile edits replace all editable fields (so optional fields can be cleared); dates of birth are `yyyy-MM-dd`.

## Differences from the original app (phase 3a)

| Original | Now |
|---|---|
| Lists (171 countries, 524 cities, phone codes, 74 nationalities, durations, day names, frequencies) hard-coded in the web code; statuses and positions fixed in the schema | **Lookups** in the database, editable per language in the back office; the web app contains no lists. |
| Messages, limits and rules (password length, lockout, booking length, 20 alerts, 4000-char messages…) hard-coded | **Runtime settings** with limits, plus **translated texts**, all editable. |
| One `admin` role inside the public site; back-office pages inside the public app | A **separate admin app** and session, **password + authenticator**, **permission-based roles**, **audit log**. |
| No record of who changed what | Every back-office change is audited in the same transaction. |

## Assumptions and decisions needing product-owner confirmation

0. **Phase 3a decisions you gave (recorded):** separate admin app on the same API/database; mandatory two-factor; permission-based roles stored as data; audit of every change; runtime-editable business rules with security floors that apply to form accounts only; city → country → global fallback for country-specific settings (used from 3b); English + Arabic with right-to-left; brand, catalog and lookups as configuration; ads (3 positions), premium and payments admin-only with calendar-date ranges; manual USD payments with receipts; e-mail first (provider configurable); admin can read and join all chats (audited, highlighted); accounts are disabled, never deleted; start with a fresh database; mobile app later. Open points for 3b are listed in the roadmap above.
0b. **Please review:** the Arabic names for countries, cities and nationalities (drafts); the starter roles *Support / Operations / Content*; the default limits in **Settings**; the public sessions now have a *fixed* length (`Auth.SessionHours`) instead of sliding.

1. **MySQL 8 is required.** The source app ran on TiDB, which does not support stored procedures. Production MySQL hosting is undecided. No data migration from TiDB was done (fresh database).
2. **Local accounts start with an unverified email.** There is no email-verification or password-reset flow yet (needs a mail provider). Google accounts are verified by Google; linking a verified Google identity marks the matching account verified.
3. **Registration reveals whether an email is taken** (`409`) — the usual usability/enumeration trade-off; rate limiting mitigates it.
4. **Security defaults** (5 attempts / 15 min lockout, 8 h sessions, 10-char passwords) are conventional choices; adjust to policy.
5. **Admin bootstrap is a CLI command**, not a UI flow.
6. **OpenAPI-generated client** (planned for phase 4) is deferred; phase 1 uses a small hand-written typed client in `web/src/api.ts`.
7. **Languages (English/Arabic), mobile app** — not part of this phase.
8. **Availability is advisory** (shown to clients, as in the original); bookings are only blocked by existing appointments. Enforcing availability needs a per-professional time zone.
9. **Public profile fields.** Email, phone and date of birth are no longer public. Say if phone numbers should be visible to signed-in clients.
10. **Starter catalog** (`V004`) is placeholder data. Replace it with the real categories/services; the back office to manage them is phase 3.
11. **Deferred:** profile/listing **image uploads** and chat **image/video** messages (needs a storage decision: object storage vs. the original's database-stored images); **e-mail/owner notifications** for new bookings and contact messages (needs a mail provider); chat uses **5-second polling**, not WebSockets.
12. **Premium and starred** flags currently only affect search ranking and are set by admins; fees, payment batches and expiry come with phase 3.
