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
