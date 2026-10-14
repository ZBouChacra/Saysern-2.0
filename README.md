# SaySerné — two-tier refactor

A Lebanese professional-services marketplace, refactored into:

| Tier | Technology | Responsibility |
|---|---|---|
| **Web** (`web/`) | TypeScript · React 19 · Vite | User interface only. Talks to the API over `/api`; never to the database or to Google directly. |
| **Application / back office** (`api/Sayserne.Api`) | C# · ASP.NET Core (.NET 10) | API endpoints, business rules, authentication, authorisation, admin functions. |
| **Database** (`db/`) | MySQL 8 | Tables and **stored procedures**, versioned as migrations. |

```
Browser ──/api──▶ ASP.NET Core API ──CALL sp_*()──▶ MySQL 8
   ▲                  │  cookie session, CSRF check, rate limits
   │                  └─ Google OIDC (server-side code exchange + ID-token validation)
 React/Vite (web/)
```

> **Status: Phase 1 of 4.** This delivery contains the foundation and the **authentication + admin-user** slice, fully working end to end.
> The marketplace features (categories, professionals, search, bookings, chat, reviews, ads, payments, site config) are **not ported yet** — see [Roadmap](#roadmap).
> The mobile app is out of scope for this refactor.

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

Open <http://localhost:5173>, register an account, and sign in.

### 5. Make yourself an admin

There is deliberately no public way to become an admin. After registering:

```powershell
dotnet run --project api/Sayserne.Migrator -- promote-admin you@example.com   # needs SAYSERNE_MIGRATOR_CONNECTION
```

Sign in again (existing sessions don't carry the new role). A **Back office** link appears; admins can search users, lock/unlock them and change roles.

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
db/migrations/R__sp_users.sql              repeatable: stored procedures, re-applied whenever the file changes
db/migrations/R__sp_identities.sql
db/migrations/R__sp_admin_users.sql
db/setup/provision.sql / deprovision.sql   database + the two accounts (templated, validated)
```

* **`V###__name.sql`** — schema changes. Applied exactly once, in order. Editing one after it was applied is **detected and rejected** (checksum, line-ending-insensitive). Add a new `V###` instead.
* **`R__name.sql`** — stored procedures. Each starts with `DROP PROCEDURE IF EXISTS` then `CREATE PROCEDURE`. Change the file, run `migrate`, and the new definition is deployed. This is how procedures reach every environment.
* A MySQL advisory lock stops two migrators running concurrently. DDL in MySQL auto-commits, so keep each `V` script small and ideally re-runnable.
* Procedures signal business errors with `SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'CODE'`; the executor turns that into an `AppException` with an HTTP status (`Infrastructure/AppException.cs`).

**Adding a feature** = (1) a `V###` for tables, (2) an `R__sp_*.sql` for its procedures, (3) a repository method that calls the procedure by name, (4) an endpoint. The guard test fails if step 3 names a procedure that step 2 doesn't define.

---

## Authentication and authorisation

* **Registration** (`POST /api/auth/register`): validated server-side (email format, 10–128-char password with a letter and a digit, names). Passwords are hashed with **PBKDF2-HMAC-SHA256** (600 000 iterations, random 128-bit salt, constant-time compare). Duplicate email → `409`.
* **Login** (`POST /api/auth/login`): same error for unknown email and wrong password, with a dummy hash verified for unknown emails to even out timing. **5 failures lock the account for 15 minutes** (counted in the database).
* **Session:** ASP.NET Core cookie auth — `HttpOnly`, `SameSite=Lax`, `Secure` outside Development, 8 h sliding. On **every request** the account is re-read: locking a user or changing their role takes effect immediately and ends stale sessions.
* **CSRF:** state-changing `/api` requests must come from an allowed origin (same origin or `Web:AllowedOrigins`), or, if the browser sent no `Origin`, carry the `X-Requested-With` header that cross-site forms cannot set. `SameSite=Lax` is a second layer.
* **Rate limiting:** auth endpoints are limited per client IP (30/min default).
* **Authorisation:** every `/api/admin/*` route requires the `AdminOnly` policy (authenticated **and** role `admin`). Anonymous → `401`, regular user → `403`. Admins cannot lock or demote themselves, and the **last active admin can never be locked or demoted** (enforced inside the stored procedures, under a row lock).
* **Open redirects:** post-login return URLs must be local paths.
* Responses never include password hashes; they use a separate `UserDto`.

### API summary

| Method & path | Access | Notes |
|---|---|---|
| `GET /api/health` | public | Calls `sp_health_ping`. |
| `GET /api/auth/providers` | public | `{ google, passwordRegistration }` |
| `POST /api/auth/register` · `login` · `logout` | public / session | |
| `GET /api/auth/me` | session | Fresh user from the database. |
| `GET /api/auth/google/start` | public (`?mode=link` needs a session) | Redirect to Google. |
| `GET /api/auth/google/callback` | public | OAuth redirect target. |
| `POST /api/auth/google/link-pending` | pending-link cookie + password | |
| `GET /api/admin/users?search&page&pageSize` | **admin** | |
| `POST /api/admin/users/{id}/lock` · `/role` | **admin** | |

---

## Configuration reference

All settings can be environment variables (`Section__Key`), user-secrets, or `appsettings.*.json` — but **secrets only via environment variables or user-secrets**. See `.env.example` for the full list.

| Key | Purpose | Default |
|---|---|---|
| `ConnectionStrings:Default` | API → MySQL (the `EXECUTE`-only account). **Required**; the API refuses to start without it. | — |
| `Google:ClientId` / `ClientSecret` / `RedirectUri` | Google sign-in | empty (feature off) |
| `Web:AllowedOrigins` | Extra origins allowed to POST (CSRF) | `http://localhost:5173` |
| `Auth:MaxFailedAttempts` / `LockoutMinutes` / `SessionHours` / `AuthRequestsPerMinute` / `PasswordIterations` | Security tuning | 5 / 15 / 8 / 30 / 600000 |
| `Proxy:TrustForwardedHeaders` | Honour `X-Forwarded-For/Proto` | `false` |
| `DataProtection:KeysPath` | Folder for encryption keys. Set it in production and share it across instances (framework default is per-machine and can be ephemeral in containers) | framework default |

### Production notes

* Serve the built web app and the API **under one origin** (reverse proxy `/api` → API). The Google redirect URI is then `https://<domain>/api/auth/google/callback` and cookies stay first-party.
* Run with `ASPNETCORE_ENVIRONMENT=Production` (cookies become `Secure`; HTTPS required). If TLS terminates at a proxy, set `Proxy__TrustForwardedHeaders=true` **only** when the API is reachable exclusively through that proxy.
* Persist and protect the Data Protection key folder (`DataProtection__KeysPath`) and share it across instances.
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
```

What the .NET suite covers: password hashing; input validation; Google ID-token validation (bad signature, wrong audience/issuer, expiry, nonce, `alg: none`, unknown key); the complete Google flow against an in-process fake Google that enforces the client secret and PKCE (new user, returning user, state mismatch, replay, unverified email, account linking with and without the password, explicit linking, locked accounts); registration/login/lockout/logout; CSRF and rate limiting; admin authorisation (anonymous 401, user 403, immediate effect of lock/role changes, last-admin protection); migrations (idempotent, tamper detection, repeatable redeploy, naming rules); the app account being refused direct table access; SQL-injection payloads treated as data; the architecture guard.

CI (`.github/workflows/ci.yml`) runs both suites against a MySQL 8 service container.

---

## Roadmap

| Phase | Scope | Status |
|---|---|---|
| 1 | Database foundation, migrations, auth (form + Google), admin users, web shell | **done (this repo)** |
| 2 | Public features: categories/services, professionals, search, profiles, availability, booking, chat, reviews, alerts | not started |
| 3 | Admin features: categories, reviews, ads, premium/fees, payments, contact messages, site config | not started |
| 4 | Web tier ported to the new API (the existing React pages), typed client generated from OpenAPI | not started |

## Assumptions and decisions needing product-owner confirmation

1. **MySQL 8 is required.** The source app ran on TiDB, which does not support stored procedures. Production MySQL hosting is undecided. No data migration from TiDB was done (fresh database).
2. **Local accounts start with an unverified email.** There is no email-verification or password-reset flow yet (needs a mail provider). Google accounts are verified by Google; linking a verified Google identity marks the matching account verified.
3. **Registration reveals whether an email is taken** (`409`) — the usual usability/enumeration trade-off; rate limiting mitigates it.
4. **Security defaults** (5 attempts / 15 min lockout, 8 h sessions, 10-char passwords) are conventional choices; adjust to policy.
5. **Admin bootstrap is a CLI command**, not a UI flow.
6. **OpenAPI-generated client** (planned for phase 4) is deferred; phase 1 uses a small hand-written typed client in `web/src/api.ts`.
7. **Languages (English/Arabic), mobile app** — not part of this phase.
