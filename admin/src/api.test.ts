import { afterEach, describe, expect, it, vi } from "vitest";
import { ApiError, api, errorMessage, setTexts } from "./api";

function mockFetch(status: number, body?: unknown) {
  const fn = vi.fn(async () => new Response(status === 204 ? null : JSON.stringify(body ?? null), { status }));
  vi.stubGlobal("fetch", fn);
  return fn;
}
afterEach(() => { vi.unstubAllGlobals(); setTexts({}); });

describe("request plumbing", () => {
  it("sends JSON with the CSRF marker header and same-origin credentials", async () => {
    const fn = mockFetch(200, { status: "totp_required" });
    await api.login("a@b.test", "pw");
    const [url, init] = fn.mock.calls[0] as unknown as [string, RequestInit];
    expect(url).toBe("/api/admin/auth/login");
    expect(init.method).toBe("POST");
    expect(init.credentials).toBe("same-origin");
    expect((init.headers as Record<string, string>)["X-Requested-With"]).toBe("sayserne-admin");
    expect(JSON.parse(init.body as string)).toEqual({ email: "a@b.test", password: "pw" });
  });
  it("treats 204 as success with no body", async () => {
    mockFetch(204);
    await expect(api.logout()).resolves.toBeUndefined();
  });
  it("turns an error response into an ApiError carrying the code and field errors", async () => {
    mockFetch(400, { error: "VALIDATION_FAILED", errors: { name: "Required." } });
    const err = await api.saveRole(null, { name: "", permissions: [] }).catch(e => e);
    expect(err).toBeInstanceOf(ApiError);
    expect(err.status).toBe(400); expect(err.code).toBe("VALIDATION_FAILED"); expect(err.fieldErrors).toEqual({ name: "Required." });
  });
  it("maps bare 403 and 429 responses to codes", async () => {
    mockFetch(403); expect((await api.settings().catch(e => e)).code).toBe("FORBIDDEN");
    mockFetch(429); expect((await api.settings().catch(e => e)).code).toBe("TooManyRequests");
  });
});

describe("query strings and routes", () => {
  it("omits empty filters and encodes keys", async () => {
    const fn = mockFetch(200, { items: [], total: 0, page: 1, pageSize: 25 });
    await api.audit({ search: "", action: "settings.update", page: 2, pageSize: 25 });
    expect((fn.mock.calls[0] as unknown as [string])[0]).toBe("/api/admin/audit?action=settings.update&page=2&pageSize=25");
    mockFetch(200, {}); const fn2 = mockFetch(200, {});
    await api.setSetting("Auth.PasswordMinLength", "12");
    expect((fn2.mock.calls[0] as unknown as [string])[0]).toBe("/api/admin/settings/Auth.PasswordMinLength");
    const fn3 = mockFetch(200, {}); await api.lookupValues("city", "LB");
    expect((fn3.mock.calls[0] as unknown as [string])[0]).toBe("/api/admin/lookups/city/values?parent=LB&includeInactive=true");
  });
});

describe("error messages", () => {
  it("prefers the translated text served by the API", () => {
    setTexts({ "error.SLOT_TAKEN": "Taken!" });
    expect(errorMessage(new ApiError(409, "SLOT_TAKEN"))).toBe("Taken!");
  });
  it("falls back to back-office wording, then to a generic message, never to the raw code", () => {
    expect(errorMessage(new ApiError(401, "INVALID_TWO_FACTOR_CODE"))).toMatch(/authenticator/i);
    const unknown = errorMessage(new ApiError(500, "SOMETHING_NEW"));
    expect(unknown).not.toContain("SOMETHING_NEW");
    expect(errorMessage(new ApiError(403, "WHATEVER"))).toMatch(/permission/i);
    expect(errorMessage(new TypeError("Failed to fetch"))).toMatch(/network/i);
  });
});
