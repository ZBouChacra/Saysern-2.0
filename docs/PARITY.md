# Comparison with the original app

The new project was compared, procedure by procedure and page by page, with the original (`saysearne` at commit `dd2e513`: React + tRPC + MySQL/TiDB, sign-in through an external OAuth service). This page records what each original feature became, what was deliberately **not** carried over, and what the original never finished.

## 1. Every original feature and where it lives now

### Visitors and members

| Original | Now |
|---|---|
| `auth.register / login / me / logout`, sign-in through the external OAuth service | Own accounts (e-mail + password, rules from settings, lockout after failed attempts) and **Google sign-in**; `POST /api/auth/*` |
| Forgot / reset password, e-mail verification *(listed but never built)* | Built: single-use links, same answer for unknown addresses, sessions end on reset |
| `categories.*`, `allServices` | `GET /api/catalog/categories`, `/services`; maintained in the back office (**Catalog**) |
| `professionals.top / search / profile` | `GET /api/professionals/top`, `/search`, `/{id}` (search ranks premium, then featured, then the visitor's own city and country) |
| `professionals.pendingSlots`, `appointments.checkOverlap` | `GET /api/professionals/{id}/busy` (times only); overlaps are refused under a row lock |
| `profile.update`, add / update / delete profession, `myProfessions` | `PUT /api/me/profile`, `/api/me/professions` |
| `profile.setAvailability / getAvailability` | `PUT/GET /api/me/availability`: weekly hours, now **enforced when booking** (judged in the professional's time zone; none set = always open) |
| `appointments.create / list / updateStatus` | `POST/GET /api/appointments`, `POST /{id}/status`: a state machine (a client can only cancel), unanswered requests **expire**, approved ones send **reminders** |
| `chat.rooms / startChat / messages / send / uploadMedia / unreadCount` | `/api/chat/*` and `/api/files`: images, short videos (compressed server-side) and location; **delete a message or a conversation** |
| `reviews.byProfessional / canReview / create` | `GET /api/professionals/{id}/reviews`, `/api/reviews/eligible`, `POST /api/reviews`; the professional can **answer** a review |
| `alerts.list / create / delete / run` | `/api/alerts` (+ **e-mailed digests** of new matches, by frequency) |
| `ads.active` | `GET /api/ads` with rotation, click and impression statistics |
| `contact.send` | `POST /api/contact` (spam trap, rate limit, optional e-mail to staff) |
| `siteConfig.get` (About text, etc.) | Texts (About, Terms, Privacy, every label), editable in both languages |
| Settings page: notification preferences, day/night mode | Settings: language, time zone, theme (system / light / dark), per-event notification choices, password change, disable account |
| English + Arabic with RTL | Complete in both apps, e-mails and receipts |

### Back office

| Original (`admin.*`) | Now |
|---|---|
| `stats` | **Dashboard**: headline numbers, month-by-month year view, CSV download |
| `users`, `userDetail` | **Users** list and a **details** panel (profile, listings with hours, appointments, counts; each opening is audited) |
| `toggleLock`, `toggleStarred` | Lock / unlock, feature (star) |
| `updateRole` (user / admin) | **Roles and permissions** as data, plus super admin; mandatory two-factor sign-in |
| `togglePremium`, `premiumReport`, `premiumBatches.*`, `listingOrderConfig.*`, `feeConfig` (premium) | **Premium**: bookings by calendar days, per-country and per-city fees and limits, capacity preview, first-paid-keeps-its-days |
| `ads.*`, `adBatches.*`, `feeConfig` (ads) | **Ads** and **Ad prices**: pictures, batches by calendar days, hard capacity per position and place |
| `payments.list`, `profitData` | **Payments**: numbered, printable receipts (English or Arabic), void instead of delete, totals; the year view of the dashboard |
| `categories.*`, `services.*` | **Catalog** |
| `contacts`, `contactReply` | **Contact messages** inbox; replies are e-mailed in the sender's language |
| `reviews.list / delete` | **Reviews**: hide (reason required) and restore; the rating follows |
| `chatRooms`, `chatMessages`, `sendMessage` | **Conversations**: read everything including deleted messages (every reading audited), write as the team |
| `siteConfig.get / set` | **Settings** (typed, validated, with limits), **Texts**, **Lookups** (lists), **Brand** |

## 2. Deliberately different

| Original | Now, and why |
|---|---|
| Sign-in through an external OAuth service | Removed (the project is self-contained). Own accounts + Google. |
| tRPC, Drizzle ORM, TiDB | A REST API (described in `docs/openapi.json`), **stored procedures only** (the API's database account cannot touch tables), MySQL 8 (TiDB has no stored procedures). |
| `togglePremium` and a per-user fee (`updateFee`) | Premium is a **paid booking for calendar days**, priced per country/city; there is no manual switch. (The original's own to-do list says the toggle was to go: "user becomes premium after batch creation + payment".) |
| Editing a booking batch's dates (`premiumBatches.update`, `adBatches.update`) | Not offered: **cancel and re-book**. This keeps the receipt, the fee that was agreed and the allocation of days exact. A paid batch's cancellation voids its receipt (the number stays on record). |
| Admin **creates** a user (`createUser`) | Not offered. Staff sign up like anyone, then receive a role (or `promote-admin`). Creating accounts with a password someone else chose is avoided on purpose; an **invitation** flow could be added if you need it. |
| Deleting a review | **Hide** with a reason (restorable). Reviews are evidence; nothing is erased. |
| The component showcase page | Dropped (a developer page). |
| "Mobile and web notification to the professional" | E-mail now (verified addresses only, per-event choices). Push notifications come with the mobile app. |
| Capacitor wrapper for Android / iOS | Not started: the mobile app is the next phase, built on `docs/openapi.json`. |

## 3. Unfinished in the original, done now

Forgot password and reset · account lockout · e-mail verification · **system jobs monitoring** (Background jobs page, retry) · **automatic expiry of pending appointments** · **appointment reminders** · **share a professional's profile link** · calendar-day batches for premium and ads · a **country-and-city-based** ordering of results · country/city dropdowns for configuration.

## 4. Beyond the original

Security corrections found while reading the original (each with a regression test) are listed in the README ("Differences from the original app"). New capability: files and media pipeline, e-mail system, saved-search digests, payments and receipts, review answers and moderation, chat moderation, contact inbox, dashboard, audit log, branding, time zones, account disabling, two-factor sign-in for staff, and the deployment guide.
