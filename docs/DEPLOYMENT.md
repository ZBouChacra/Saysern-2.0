# Deploying SaySerné

This guide takes you from an empty server to a running site, on **Linux** (systemd + nginx) or **Windows** (IIS). The two paths differ only in how the API is hosted and how web traffic reaches it; everything else is the same.

> **What has and has not been tested.** The application itself, its database scripts and both web apps are covered by automated tests and browser journeys (see the README). The files in `deploy/` are standard, conventional configurations that have been syntax-checked (the systemd unit with `systemd-analyze`, the IIS files as XML, the backup script with `bash -n`), but **they have not been run on a real nginx or IIS server**. Expect to adjust paths and host names, and test on a staging copy first.

## 1. How the pieces fit

```
Visitors ──https──> example.com        ┐  static files (web/dist)
                                       ├─ /api/*  ──> ASP.NET Core API (127.0.0.1:5080) ──> MySQL 8
Staff    ──https──> admin.example.com  ┘  static files (admin/dist), /api/* to the SAME API
```

* **Two host names, one API.** The public site and the back office are separate sites (separate cookies, separate access rules). Both forward `/api` to the same API process, so cookies stay first-party and there is no cross-site traffic.
* **The API is the only thing that talks to the database,** and only by calling stored procedures, with an account that has no table access.
* Files (photos, videos) go to the server's disk or to an S3-compatible store; e-mail goes out through an SMTP server you choose.

## 2. What to prepare

| Need | Notes |
|---|---|
| A server (or two) | 2 CPU / 4 GB RAM is a comfortable start. Video processing is the heavy part. |
| **MySQL 8.0+** | Not TiDB or other look-alikes (no stored procedures). `max_connections` comfortably above the API's connection pool (100 per API instance by default), plus a few for backups and the migrator. |
| **.NET 10 runtime** | Linux: `dotnet-aspnetcore-runtime-10.0`. Windows: the **.NET 10 Hosting Bundle** (installs the IIS module). |
| **ffmpeg and ffprobe** | Required. Every photo and video is processed with them. |
| Two host names + TLS certificates | e.g. `example.com` and `admin.example.com`. Let's Encrypt works. |
| An SMTP account | For verification, password reset, notifications and contact replies (587 with STARTTLS; port 465 "implicit TLS" is not supported). |
| Node 22 + npm | Only on the machine that **builds** the two web apps (this can be your laptop or a CI job). |
| Google OAuth client (optional) | Only if you want "Continue with Google". Redirect URI: `https://example.com/api/auth/google/callback`. |

## 3. Build

On a build machine with the repository checked out:

```bash
dotnet publish api/Sayserne.Api      -c Release -o publish/api
dotnet publish api/Sayserne.Migrator -c Release -o publish/migrator
(cd web   && npm ci && npm run build)    # -> web/dist
(cd admin && npm ci && npm run build)    # -> admin/dist
```

The web apps call `/api/...` on their own host name, so **nothing about your domain is compiled in**: the same build works on staging and production.

## 4. The database (once)

Create the schema owner and the API's account. Run on any machine that can reach MySQL, with a MySQL administrator's connection:

```bash
export SAYSERNE_ADMIN_CONNECTION="Server=db.internal;Port=3306;User ID=root;Password=<root password>"
export SAYSERNE_DB_NAME=sayserne SAYSERNE_MIGRATOR_USER=sayserne_migrator SAYSERNE_APP_USER=sayserne_app
export SAYSERNE_MIGRATOR_PASSWORD='<12-64 chars>' SAYSERNE_APP_PASSWORD='<12-64 chars, different>'
export SAYSERNE_USER_HOST='%'     # only when the API runs on a different machine than MySQL
dotnet publish/migrator/Sayserne.Migrator.dll provision
```

* `sayserne_migrator` owns the schema. **Keep its password out of the API's environment.**
* `sayserne_app` may only `EXECUTE` stored procedures. If the API is ever compromised, it still cannot read or alter tables directly.
* Passwords may use letters, digits and `. _ ~ ! @ # % ^ * + = : , -`.
* Firewall MySQL so only the API server (and your backup job) can reach it.

Then deploy the tables and procedures (this is also **step one of every upgrade**):

```bash
export SAYSERNE_MIGRATOR_CONNECTION="Server=db.internal;Port=3306;Database=sayserne;User ID=sayserne_migrator;Password=<migrator password>"
dotnet publish/migrator/Sayserne.Migrator.dll migrate
```

`migrate` is safe to run any number of times; it applies only what is new.

## 5. Configure the API

Settings are environment variables. A full annotated list is in `.env.example`; the ones you must set are:

| Variable | Value |
|---|---|
| `ASPNETCORE_ENVIRONMENT` | `Production` (makes cookies `Secure`, requires HTTPS) |
| `ConnectionStrings__Default` | `Server=db.internal;Port=3306;Database=sayserne;User ID=sayserne_app;Password=<app password>` |
| `Web__AllowedOrigins__0` | `https://example.com` |
| `Admin__AllowedOrigins__0` | `https://admin.example.com` |
| `Proxy__TrustForwardedHeaders` | `true` — **only** because the API listens on `127.0.0.1` and is reachable solely through your proxy |
| `DataProtection__KeysPath` | A persistent folder, e.g. `/var/lib/sayserne/dp-keys` (see the warning below) |
| `Storage__DiskPath` | A persistent folder for uploads, e.g. `/var/lib/sayserne/storage` (unless you use S3) |
| `Media__FfmpegPath` / `Media__FfprobePath` | Full paths if they are not on the service's `PATH` |
| `Google__ClientId`, `Google__ClientSecret`, `Google__RedirectUri` | Only for Google sign-in |

> **Protect the Data Protection keys.** They encrypt every back-office user's two-factor secret and the stored e-mail password. If they are lost, every admin must re-enrol (another super admin can reset them, or use the `reset-2fa` command) and the e-mail password must be typed in again. Put the folder on persistent storage, restrict it to the service account, and **include it in your backups** (the backup script does).

Business rules (lockout, session length, rate limits, booking limits, fees…) are **not** environment variables: they are runtime settings edited in the back office.

## 6A. Linux (systemd + nginx)

```bash
# service account and folders
sudo useradd --system --home /var/lib/sayserne --shell /usr/sbin/nologin sayserne
sudo mkdir -p /opt/sayserne/api /var/lib/sayserne/{storage,dp-keys} /etc/sayserne /var/www/sayserne/{web,admin}
sudo chown -R sayserne:sayserne /var/lib/sayserne && sudo chmod 700 /var/lib/sayserne/dp-keys

# files
sudo rsync -a --delete publish/api/ /opt/sayserne/api/
sudo rsync -a --delete web/dist/    /var/www/sayserne/web/
sudo rsync -a --delete admin/dist/  /var/www/sayserne/admin/

# configuration (secrets): readable by root and the service only
sudo install -m 640 -o root -g sayserne /dev/null /etc/sayserne/api.env
sudoedit /etc/sayserne/api.env        # one VARIABLE=value per line, as in the table above

# service
sudo cp deploy/linux/sayserne-api.service /etc/systemd/system/
sudo systemctl daemon-reload && sudo systemctl enable --now sayserne-api
curl -s http://127.0.0.1:5080/api/health

# web server: edit the host names and certificate paths first
sudo cp deploy/linux/nginx-web.conf   /etc/nginx/sites-available/sayserne-web
sudo cp deploy/linux/nginx-admin.conf /etc/nginx/sites-available/sayserne-admin
sudo ln -s /etc/nginx/sites-available/sayserne-{web,admin} /etc/nginx/sites-enabled/
sudo nginx -t && sudo systemctl reload nginx
```

Install ffmpeg with `sudo apt install ffmpeg`.

## 6B. Windows (IIS)

1. Install the **.NET 10 Hosting Bundle**, the IIS **URL Rewrite** module and **Application Request Routing**; in IIS Manager enable *Application Request Routing Cache → Server Proxy Settings → Enable proxy*. Install ffmpeg and note the folder with `ffmpeg.exe` and `ffprobe.exe`.
2. **The API site.** Copy `publish/api` to `C:\sayserne\api`. In IIS create a site (e.g. "SaySerne API") on that folder, bound to `http`, **IP address 127.0.0.1, port 5080**, with its own application pool set to *No Managed Code*. (The published folder already contains a `web.config` that hosts the app through the ASP.NET Core Module.) Give the pool's identity (`IIS AppPool\SaySerne API`) *modify* rights on the storage and keys folders (e.g. `C:\sayserne\storage` and `C:\sayserne\dp-keys`) and nothing else.
3. **The environment variables** from section 5 (`ASPNETCORE_ENVIRONMENT=Production`, the connection string, origins, `DataProtection__KeysPath=C:\sayserne\dp-keys`, `Storage__DiskPath=C:\sayserne\storage`, `Media__FfmpegPath=C:\tools\ffmpeg\bin\ffmpeg.exe`, …): set them as **machine-level environment variables**, then restart the application pool. (Avoid writing secrets into `web.config`: it is easy to copy by accident.)
4. **The public site.** Create an IIS site for `example.com` on a folder holding the contents of `web/dist` plus `deploy/iis/web/web.config`; bind it to HTTPS with your certificate.
5. **The back office.** The same for `admin.example.com`, with `admin/dist` and `deploy/iis/admin/web.config`.
6. Test: `http://127.0.0.1:5080/api/health` on the server, then `https://example.com/api/health` from outside.
7. Back up with the Windows Task Scheduler running `mysqldump` and copying the storage and key folders (`deploy/backup.sh` shows exactly what to include).

## 7. First run

1. Open the **public site**, register your own account, then make it a super admin (there is deliberately no public way to become one):
   ```bash
   dotnet publish/migrator/Sayserne.Migrator.dll promote-admin you@example.com   # needs SAYSERNE_MIGRATOR_CONNECTION
   ```
2. Open the **back office**, sign in, and enrol your authenticator app. **Save the recovery codes.**
3. Work through the back office top to bottom:

| Where | What to set |
|---|---|
| **Settings → Site** | `Site.PublicUrl` (`https://example.com`: used in every e-mail link), `Site.Name`, `Site.DefaultLanguage`, `Site.DefaultTimeZone` |
| **Settings → Email** | Provider `smtp`, host, port (587), security, user, password, from-address. Then **Send test email**. |
| **Settings → Storage** | `disk` or `s3` (and the S3 values in the environment) |
| **Settings → Billing** | Company name, address, tax id, VAT %, receipt prefix and footer: they appear on every receipt |
| **Settings → Contact** | `Contact.NotifyAddress` to be e-mailed when someone writes |
| **Brand** | Your colours and logo |
| **Texts** | Read and adapt the wording in both languages. **The Terms of use and Privacy policy are drafts: have a lawyer review them.** Review the Arabic too. |
| **Catalog** | Replace the placeholder categories and services |
| **Roles** | Create roles for your team and assign them (staff can sign in once they hold a role) |

## 8. Security checklist

* [ ] HTTPS everywhere, HSTS on; HTTP redirects to HTTPS.
* [ ] The API listens only on `127.0.0.1`; only nginx/IIS faces the internet.
* [ ] MySQL is not reachable from the internet; `sayserne_app` is the only account the API knows.
* [ ] `/etc/sayserne/api.env` (or the machine environment) is readable only by admins and the service.
* [ ] The back office is on its own host name, ideally IP-restricted or behind a VPN.
* [ ] Data Protection keys are on persistent storage, access-restricted and **backed up**.
* [ ] Every staff member has their own account (no sharing) and a role with only what they need.
* [ ] Operating-system and .NET security updates are applied (subscribe to the .NET release notes).
* [ ] A backup has been **restored on another machine at least once** (an untested backup is a hope, not a backup).

## 9. Backups and restoring

`deploy/backup.sh` writes three files per run: the database dump, the uploaded files (when you use disk storage) and the Data Protection keys. Schedule it nightly, **copy the results off the server**, and keep at least two weeks.

To restore on a clean machine: install the prerequisites (section 2), restore the keys folder and the files folder, create an empty database with `provision`, load the dump (`gunzip -c db-….sql.gz | mysql sayserne`), run `migrate` (a no-op if the dump is current), start the API.

## 10. Upgrading

1. Back up (section 9).
2. Build the new version (section 3).
3. **Run `migrate` first**, then replace the API files and restart it, then replace the web files. A migration can change the database in a way the *previous* API version does not understand (an earlier release removed a column), so for the minute or two between `migrate` and the restart the site may show errors: do upgrades at a quiet time, and put up a maintenance page if you want to be tidy.
4. Check `/api/health` and the back office's *Background jobs* and *Email log* pages for failures.

To roll back, restore the backup taken in step 1 (database **and** files) and put the previous build back. The database changes are not reversible by running the old version.

## 11. Running more than one API instance

Supported, with three conditions: all instances share the **same** `DataProtection__KeysPath` (a shared or replicated folder), uploaded files go to **S3** (or a shared disk), and each has the same environment. Runtime settings propagate to every instance within `Settings__RefreshSeconds` (15 by default). Scheduled work (digests, cleanups, premium re-allocation) is queued with a database-enforced unique key per day or period, so it is queued once however many instances run their schedulers.

## 12. Troubleshooting

| Symptom | Likely cause |
|---|---|
| The API will not start; the log says the connection string is missing | `ConnectionStrings__Default` is not set in the service's environment |
| Everyone is signed out after every restart | `DataProtection__KeysPath` is not set or not persistent |
| Sign-in works but the next click says you are signed out / "forbidden" | The proxy does not forward `X-Forwarded-Proto` or `Proxy__TrustForwardedHeaders` is off: cookies are marked `Secure` and the API thinks the request is plain HTTP |
| A form or button is refused as coming from the wrong origin (a cross-site-request check) | `Web__AllowedOrigins__0` / `Admin__AllowedOrigins__0` does not exactly match the address in the browser (scheme + host, no trailing slash) |
| Photos upload but never appear / videos stay "processing" | ffmpeg is missing or its path is wrong (`Media__FfmpegPath`); check *Background jobs* |
| No e-mails arrive | Settings → Email is still `off`, wrong SMTP values, or port 465 (use 587). The *Email log* page shows the error for each message |
| E-mail links point to `localhost` | `Site.PublicUrl` is not set |
| Large uploads fail with 413 | The proxy limit (`client_max_body_size` / IIS `maxAllowedContentLength`) is below the Media limits in Settings |
| The back office page is blank after a deployment | A stale cached `index.html`: make sure it is served with `no-store` (the provided configs do) |
| Arabic text appears left-to-right | The language lookup `ar` lost its direction metadata (Lookups → Languages → `{"dir":"rtl"}`) |
