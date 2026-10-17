import { afterEach, describe, expect, it, vi } from "vitest";
import { ApiError, api, errorMessage, fromQuery, googleLinkUrl, googleSignInUrl, toQuery } from "../api";
import { setTexts } from "../texts";
import { validateRegistration } from "../validation";

function mockFetch(status: number, body?: unknown) {
  const fn = vi.fn(async () => new Response(body === undefined ? null : JSON.stringify(body), { status }));
  vi.stubGlobal("fetch", fn);
  return fn;
}
afterEach(() => vi.unstubAllGlobals());

describe("api client", () => {
  it("sends credentials, JSON and the CSRF header", async () => {
    const fn = mockFetch(200, { user: { id: 1 } });
    await api.login("a@b.co", "pw");
    const [url, init] = fn.mock.calls[0] as unknown as [string, RequestInit];
    expect(url).toBe("/api/auth/login");
    expect(init.method).toBe("POST");
    expect(init.credentials).toBe("same-origin");
    expect((init.headers as Record<string, string>)["X-Requested-With"]).toBe("sayserne-web");
    expect(JSON.parse(init.body as string)).toEqual({ email: "a@b.co", password: "pw" });
  });

  it("turns error responses into ApiError with code and field errors", async () => {
    mockFetch(400, { error: "VALIDATION_FAILED", errors: { password: "Use at least 10 characters." } });
    const err = await api.register({ email: "a@b.co", password: "x", firstName: "A", lastName: "B" }).catch(e => e);
    expect(err).toBeInstanceOf(ApiError);
    expect(err.status).toBe(400);
    expect(err.code).toBe("VALIDATION_FAILED");
    expect(err.fieldErrors?.password).toContain("10 characters");
  });

  it("handles 204 No Content", async () => {
    mockFetch(204);
    await expect(api.logout()).resolves.toBeUndefined();
  });

  it("the public client contains no back-office calls (the back office is its own app)", () => {
    for (const name of ["adminUsers", "setLocked", "setRole", "setFlags"]) expect(name in api).toBe(false);
  });

  it("takes error texts from what the API serves, and never leaks unknown codes", () => {
    setTexts({ "error.INVALID_CREDENTIALS": "Email or password is incorrect.", "error.google_denied": "Google sign-in was cancelled." });
    expect(errorMessage(new ApiError(401, "INVALID_CREDENTIALS"))).toContain("incorrect");
    expect(errorMessage("google_denied")).toContain("cancelled");
    expect(errorMessage(new ApiError(500, "SOME_INTERNAL_CODE"))).not.toContain("SOME_INTERNAL_CODE");
    expect(errorMessage(new TypeError("Failed to fetch"))).toContain("Network");
    setTexts({ "error.INVALID_CREDENTIALS": "نص مختلف" });   // an admin edit (or another language) is what the user sees
    expect(errorMessage(new ApiError(401, "INVALID_CREDENTIALS"))).toBe("نص مختلف");
  });

  it("builds Google URLs that carry only a local return path", () => {
    expect(googleSignInUrl("/admin")).toBe("/api/auth/google/start?returnUrl=%2Fadmin");
    expect(googleLinkUrl()).toBe("/api/auth/google/start?mode=link&returnUrl=%2F");
  });
});

describe("registration validation", () => {
  const ok = { email: "me@example.com", password: "correct-horse-9", firstName: "Me", lastName: "Myself" };
  const rules = { minLength: 10, requireLetter: true, requireDigit: true };   // in the app these come from /api/config/public
  it("accepts a valid form", () => expect(validateRegistration(ok, rules)).toEqual({}));
  it("rejects weak passwords", () => {
    expect(validateRegistration({ ...ok, password: "short" }, rules).password).toBeDefined();
    expect(validateRegistration({ ...ok, password: "allletterspassword" }, rules).password).toBeDefined();
    expect(validateRegistration({ ...ok, password: "12345678901234" }, rules).password).toBeDefined();
  });
  it("follows the rules the server publishes, not numbers compiled into the app", () => {
    expect(validateRegistration({ ...ok, password: "abc12345" }, { ...rules, minLength: 8 })).toEqual({});
    expect(validateRegistration({ ...ok, password: "abc12345" }, { ...rules, minLength: 12 }).password).toContain("12 characters");
    expect(validateRegistration({ ...ok, password: "onlylettershere" }, { ...rules, requireDigit: false })).toEqual({});
    expect(validateRegistration({ ...ok, password: "12345678901234" }, { ...rules, requireLetter: false })).toEqual({});
  });
  it("rejects bad emails and empty names", () => {
    expect(validateRegistration({ ...ok, email: "nope" }, rules).email).toBeDefined();
    expect(validateRegistration({ ...ok, firstName: " " }, rules).firstName).toBeDefined();
  });
});

describe("search query round-trip", () => {
  it("skips empty values and keeps the rest", () => {
    expect(toQuery({ city: "Beirut", categoryId: 3, minStars: undefined, firstName: "", hasTeam: false })).toBe("city=Beirut&categoryId=3&hasTeam=false");
  });
  it("parses numbers, booleans and text, dropping junk", () => {
    const f = fromQuery(new URLSearchParams("categoryId=3&city=%20Beirut%20&hasOffice=true&hasTeam=maybe&minStars=abc&maxCost=75.5"));
    expect(f.categoryId).toBe(3);
    expect(f.city).toBe("Beirut");
    expect(f.hasOffice).toBe(true);
    expect(f.hasTeam).toBeUndefined();
    expect(f.minStars).toBeUndefined();
    expect(f.maxCost).toBe(75.5);
  });
  it("is stable: toQuery(fromQuery(x)) keeps every valid filter", () => {
    const original = { serviceId: 9, city: "Jounieh", minExperience: 4, hasOffice: true };
    expect(fromQuery(new URLSearchParams(toQuery(original)))).toMatchObject(original);
  });
  it("lets a saved alert be replayed as a search URL", () => {
    const alert = { categoryId: 1, city: "Beirut", maxCost: 60 };
    expect(`/search?${toQuery(alert)}`).toBe("/search?categoryId=1&city=Beirut&maxCost=60");
  });
});

describe("marketplace calls", () => {
  it("search sends the filters as a query string", async () => {
    const fn = mockFetch(200, { items: [], total: 0, page: 1, pageSize: 20 });
    await api.search({ city: "Beirut", minStars: 4 });
    expect((fn.mock.calls[0] as unknown as [string])[0]).toBe("/api/professionals/search?city=Beirut&minStars=4");
  });
  it("booking posts the professional, time and duration", async () => {
    const fn = mockFetch(201, { id: 1 });
    await api.book({ professionalId: 7, startsAt: "2030-01-01T10:00:00.000Z", durationMinutes: 45 });
    const [url, init] = fn.mock.calls[0] as unknown as [string, RequestInit];
    expect(url).toBe("/api/appointments");
    expect(JSON.parse(init.body as string)).toEqual({ professionalId: 7, startsAt: "2030-01-01T10:00:00.000Z", durationMinutes: 45 });
  });
  it("status changes go to the status endpoint", async () => {
    const fn = mockFetch(200, { id: 5 });
    await api.setStatus(5, "approved");
    expect((fn.mock.calls[0] as unknown as [string])[0]).toBe("/api/appointments/5/status");
  });
  it("unread count unwraps the number", async () => {
    mockFetch(200, { unread: 3 });
    await expect(api.unread()).resolves.toBe(3);
  });
  it("a rate-limited (429, empty body) response becomes a friendly error", async () => {
    mockFetch(429);
    const err = await api.send(1, { content: "hi" }).catch(e => e);
    expect(err).toBeInstanceOf(ApiError);
    expect(err.code).toBe("TooManyRequests");
    setTexts({ "error.TooManyRequests": "You're doing that too quickly. Please wait a moment." });
    expect(errorMessage(err)).toContain("too quickly");
  });
  it("booking conflicts and role errors have specific messages", () => {
    setTexts(Object.fromEntries(Object.entries({"INVALID_CREDENTIALS": "Email or password is incorrect.", "ACCOUNT_LOCKED": "This account is locked. Try again later or contact support.", "DUPLICATE_EMAIL": "An account with this email already exists. Try signing in.", "VALIDATION_FAILED": "Please fix the highlighted fields.", "CSRF_REJECTED": "The request was blocked for your safety. Reload the page and try again.", "LINK_EXPIRED": "The Google link request expired. Start Google sign-in again.", "LINK_NOT_POSSIBLE": "This account has no password, so it can't be linked this way.", "LAST_ADMIN": "There must always be at least one active super admin.", "CANNOT_MODIFY_SELF": "You can't change your own role or lock your own account.", "USER_NOT_FOUND": "That user no longer exists.", "INVALID_ROLE": "Unknown role.", "GOOGLE_NOT_CONFIGURED": "Google sign-in isn't configured on this server.", "SLOT_TAKEN": "That time is no longer available. Please choose another.", "START_IN_PAST": "Choose a time in the future.", "CANNOT_BOOK_SELF": "You can't book yourself.", "PROFESSIONAL_NOT_FOUND": "That professional is not available for booking.", "PROFESSION_NOT_FOUND": "That service listing no longer exists.", "INVALID_DURATION": "Choose a duration within the allowed range.", "INVALID_TRANSITION": "That action isn't possible for this appointment any more.", "FORBIDDEN_ACTION": "Only the professional can do that.", "NOT_STARTED_YET": "You can complete an appointment once it has started.", "NOT_COMPLETED": "Only completed appointments can be reviewed.", "REVIEW_EXISTS": "You already reviewed this appointment.", "APPOINTMENT_NOT_FOUND": "That appointment doesn't exist.", "NOT_A_PROFESSIONAL": "Switch your profile type to professional first.", "HIDE_PROFESSIONS_FIRST": "Hide your service listings before switching back to a customer profile.", "DUPLICATE_PROFESSION": "You already offer that service.", "INVALID_SERVICE": "That service doesn't belong to the chosen category.", "ROOM_NOT_FOUND": "That conversation doesn't exist.", "CANNOT_CHAT_SELF": "You can't message yourself.", "ALERT_LIMIT": "You have reached the maximum number of saved searches.", "TooManyRequests": "You're doing that too quickly. Please wait a moment.", "SERVER_ERROR": "Something went wrong on our side. Please try again.", "google_state": "The Google sign-in session expired or was invalid. Please try again.", "google_denied": "Google sign-in was cancelled.", "google_token": "Google sign-in could not be verified. Please try again.", "google_exchange": "Could not complete Google sign-in. Please try again.", "google_email_unverified": "Your Google email address isn't verified, so it can't be used to sign in.", "google_conflict": "That sign-up collided with another one. Please try again.", "account_locked": "This account is locked. Try again later or contact support.", "login_required": "Please sign in first."} as Record<string, string>).map(([k, v]) => ["error." + k, v])));
    expect(errorMessage(new ApiError(409, "SLOT_TAKEN"))).toContain("no longer available");
    expect(errorMessage(new ApiError(403, "FORBIDDEN_ACTION"))).toContain("professional");
    expect(errorMessage(new ApiError(409, "REVIEW_EXISTS"))).toContain("already reviewed");
  });
});

describe("file uploads", () => {
  const photo = new File([new Uint8Array([0xff, 0xd8, 0xff, 0xe0])], "me.jpg", { type: "image/jpeg" });

  it("posts the file as multipart form data with the CSRF header and no hand-made content type", async () => {
    const fn = mockFetch(201, { id: "a".repeat(32), status: "ready", kind: "image", url: "/api/files/" + "a".repeat(32) });
    const result = await api.uploadFile(photo, "profile_photo");
    const [url, init] = fn.mock.calls[0] as unknown as [string, RequestInit];
    expect(url).toBe("/api/files?purpose=profile_photo");
    expect(init.method).toBe("POST");
    expect(init.credentials).toBe("same-origin");
    expect((init.headers as Record<string, string>)["X-Requested-With"]).toBe("sayserne-web");
    expect(Object.keys(init.headers as Record<string, string>)).not.toContain("Content-Type");   // the browser must add the multipart boundary itself
    const form = init.body as FormData;
    expect(form).toBeInstanceOf(FormData);
    expect((form.get("file") as File).name).toBe("me.jpg");
    expect(result.status).toBe("ready");
  });

  it("names the conversation for chat media and reports a video as still processing", async () => {
    const fn = mockFetch(202, { id: "b".repeat(32), status: "processing", kind: "video", url: "/api/files/" + "b".repeat(32) });
    const result = await api.uploadFile(photo, "chat_media", 7);
    expect((fn.mock.calls[0] as unknown as [string])[0]).toBe("/api/files?purpose=chat_media&roomId=7");
    expect(result).toMatchObject({ status: "processing", kind: "video" });
  });

  it("turns refusals into coded errors, including a bare 413 from a proxy", async () => {
    mockFetch(400, { error: "UNSUPPORTED_FILE" });
    expect((await api.uploadFile(photo, "profile_photo").catch(e => e)).code).toBe("UNSUPPORTED_FILE");
    mockFetch(413);
    const tooBig = await api.uploadFile(photo, "profile_photo").catch(e => e);
    expect(tooBig).toBeInstanceOf(ApiError);
    expect(tooBig.code).toBe("FILE_TOO_LARGE");
    mockFetch(429, { error: "TooManyRequests" });
    expect((await api.uploadFile(photo, "profile_photo").catch(e => e)).code).toBe("TooManyRequests");
  });

  it("asks for a file's status by id and shows the server's reason for a failure", async () => {
    const fn = mockFetch(200, { id: "c".repeat(32), status: "failed", kind: "video", failureCode: "VIDEO_TOO_LONG" });
    const status = await api.fileStatus("c".repeat(32));
    expect((fn.mock.calls[0] as unknown as [string])[0]).toBe("/api/files/" + "c".repeat(32) + "/status");
    setTexts({ "error.VIDEO_TOO_LONG": "This video is too long. Please choose a shorter one." });
    expect(errorMessage(status.failureCode)).toContain("too long");
  });

  it("sends an image message by file id", async () => {
    const fn = mockFetch(201, { id: 1 });
    await api.send(3, { type: "image", fileId: "d".repeat(32), content: "the invoice" });
    expect(JSON.parse((fn.mock.calls[0] as unknown as [string, RequestInit])[1].body as string)).toEqual({ type: "image", fileId: "d".repeat(32), content: "the invoice" });
  });
});

describe("account email calls", () => {
  it("posts the verification token in the body, never in the URL", async () => {
    const fn = mockFetch(200, { verified: true });
    await api.verifyEmail("tok123");
    const [url, init] = fn.mock.calls[0] as unknown as [string, RequestInit];
    expect([url, init.method]).toEqual(["/api/auth/verify-email", "POST"]);
    expect(JSON.parse(init.body as string)).toEqual({ token: "tok123" });
  });
  it("asks for a reset and sets a new password with the token", async () => {
    const f1 = mockFetch(202, { accepted: true });
    await api.forgotPassword("me@example.com");
    expect(JSON.parse((f1.mock.calls[0] as unknown as [string, RequestInit])[1].body as string)).toEqual({ email: "me@example.com" });
    const f2 = mockFetch(200, { reset: true });
    await api.resetPassword("tok", "New-pass-123");
    expect((f2.mock.calls[0] as unknown as [string])[0]).toBe("/api/auth/reset-password");
    expect(JSON.parse((f2.mock.calls[0] as unknown as [string, RequestInit])[1].body as string)).toEqual({ token: "tok", password: "New-pass-123" });
  });
  it("reads and changes notification choices", async () => {
    mockFetch(200, [{ event: "booking_requested", channel: "email", enabled: false }]);
    expect(await api.notificationPrefs()).toEqual([{ event: "booking_requested", channel: "email", enabled: false }]);
    const fn = mockFetch(200, []);
    await api.setNotificationPref("new_message", "email", false);
    const [url, init] = fn.mock.calls[0] as unknown as [string, RequestInit];
    expect([url, init.method]).toEqual(["/api/me/notifications", "PUT"]);
    expect(JSON.parse(init.body as string)).toEqual({ event: "new_message", channel: "email", enabled: false });
  });
  it("turns an invalid link into a readable message", async () => {
    mockFetch(400, { error: "INVALID_TOKEN" });
    const err = await api.verifyEmail("x").catch(e => e);
    setTexts({ "error.INVALID_TOKEN": "This link is invalid or has expired. Please ask for a new one." });
    expect(errorMessage(err)).toContain("expired");
  });
});

describe("ads", () => {
  it("asks for the ads of a position and the visitor's place, encoding what it sends", async () => {
    const fn = mockFetch(200, [{ id: 1, title: "A", position: "sidebar", imageUrl: "/api/files/x", clickUrl: "/api/ads/1/click" }]);
    const ads = await api.ads("sidebar", "LB", "Jbeil & Co");
    expect((fn.mock.calls[0] as unknown as [string])[0]).toBe("/api/ads?position=sidebar&country=LB&city=Jbeil%20%26%20Co");
    expect(ads[0].clickUrl).toBe("/api/ads/1/click");
    const bare = mockFetch(200, []);
    await api.ads("home_banner");
    expect((bare.mock.calls[0] as unknown as [string])[0]).toBe("/api/ads?position=home_banner");   // no place known: nothing extra is sent
  });
  it("reports an impression without waiting and never throws, even when the network fails", async () => {
    const fn = mockFetch(204);
    api.adImpression(7);
    const [url, init] = fn.mock.calls[0] as unknown as [string, RequestInit];
    expect([url, init.method, init.keepalive]).toEqual(["/api/ads/7/impression", "POST", true]);
    expect((init.headers as Record<string, string>)["X-Requested-With"]).toBe("sayserne-web");
    vi.stubGlobal("fetch", vi.fn(() => Promise.reject(new Error("offline"))));
    expect(() => api.adImpression(8)).not.toThrow();
    await Promise.resolve();   // the rejected promise is swallowed (an unhandled rejection would fail the run)
  });
});

describe("search with the visitor's place", () => {
  it("adds the place only for ordering and only when a country is known", async () => {
    const f1 = mockFetch(200, { items: [], total: 0, page: 1, pageSize: 20 });
    await api.search({}, { country: "LB", city: "Beirut" });
    expect((f1.mock.calls[0] as unknown as [string])[0]).toContain("visitorCountry=LB&visitorCity=Beirut");
    const f2 = mockFetch(200, { items: [], total: 0, page: 1, pageSize: 20 });
    await api.search({ city: "Jbeil" }, { city: "Beirut" });               // a city without a country means nothing: it is not sent
    expect((f2.mock.calls[0] as unknown as [string])[0]).not.toContain("visitor");
    const f3 = mockFetch(200, { items: [], total: 0, page: 1, pageSize: 20 });
    await api.search({});
    expect((f3.mock.calls[0] as unknown as [string])[0]).toBe("/api/professionals/search?");
  });
});
