import { afterEach, describe, expect, it, vi } from "vitest";
import { ApiError, api, errorMessage, fromQuery, googleLinkUrl, googleSignInUrl, toQuery } from "../api";
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

  it("encodes admin search terms", async () => {
    const fn = mockFetch(200, { items: [], total: 0, page: 1, pageSize: 20 });
    await api.adminUsers("a&b=c d", 2);
    expect((fn.mock.calls[0] as unknown as [string])[0]).toBe("/api/admin/users?search=a%26b%3Dc%20d&page=2&pageSize=20");
  });

  it("maps known codes to friendly messages and never leaks unknown ones", () => {
    expect(errorMessage(new ApiError(401, "INVALID_CREDENTIALS"))).toContain("incorrect");
    expect(errorMessage("google_denied")).toContain("cancelled");
    expect(errorMessage(new ApiError(500, "SOME_INTERNAL_CODE"))).not.toContain("SOME_INTERNAL_CODE");
    expect(errorMessage(new TypeError("Failed to fetch"))).toContain("Network");
  });

  it("builds Google URLs that carry only a local return path", () => {
    expect(googleSignInUrl("/admin")).toBe("/api/auth/google/start?returnUrl=%2Fadmin");
    expect(googleLinkUrl()).toBe("/api/auth/google/start?mode=link&returnUrl=%2F");
  });
});

describe("registration validation", () => {
  const ok = { email: "me@example.com", password: "correct-horse-9", firstName: "Me", lastName: "Myself" };
  it("accepts a valid form", () => expect(validateRegistration(ok)).toEqual({}));
  it("rejects weak passwords", () => {
    expect(validateRegistration({ ...ok, password: "short" }).password).toBeDefined();
    expect(validateRegistration({ ...ok, password: "allletterspassword" }).password).toBeDefined();
    expect(validateRegistration({ ...ok, password: "12345678901234" }).password).toBeDefined();
  });
  it("rejects bad emails and empty names", () => {
    expect(validateRegistration({ ...ok, email: "nope" }).email).toBeDefined();
    expect(validateRegistration({ ...ok, firstName: " " }).firstName).toBeDefined();
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
    expect(errorMessage(err)).toContain("too quickly");
  });
  it("booking conflicts and role errors have specific messages", () => {
    expect(errorMessage(new ApiError(409, "SLOT_TAKEN"))).toContain("no longer available");
    expect(errorMessage(new ApiError(403, "FORBIDDEN_ACTION"))).toContain("professional");
    expect(errorMessage(new ApiError(409, "REVIEW_EXISTS"))).toContain("already reviewed");
  });
});
