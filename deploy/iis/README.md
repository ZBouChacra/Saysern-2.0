# IIS files

* `web/web.config`: for the **public site** (static files from `web/dist`). Redirects to HTTPS, forwards `/api` to the API on `http://127.0.0.1:5080`, serves `index.html` for the app's own routes, sets upload limit and cache rules.
* `admin/web.config`: the same for the **back office** (`admin/dist`), with a smaller upload limit.

Requirements, in order: IIS, the **.NET 10 Hosting Bundle**, the **URL Rewrite** module, **Application Request Routing** with proxy enabled. The full step-by-step is in `docs/DEPLOYMENT.md`, section 6B.

If the API listens on a different port, change `5080` in both files. These files have been checked as well-formed XML but **not run on a real IIS server**: test them on a staging site first.
