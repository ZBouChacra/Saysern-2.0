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

describe("background jobs", () => {
  it("lists with filters, reads stats and retries by id", async () => {
    const fn = mockFetch(200, { items: [], total: 0, page: 1, pageSize: 25 });
    await api.jobs("failed", "media.process", 2);
    expect((fn.mock.calls[0] as unknown as [string])[0]).toBe("/api/admin/jobs?status=failed&type=media.process&page=2&pageSize=25");
    const stats = mockFetch(200, [{ type: "media.process", status: "done", count: 3 }]);
    expect(await api.jobStats()).toEqual([{ type: "media.process", status: "done", count: 3 }]);
    expect((stats.mock.calls[0] as unknown as [string])[0]).toBe("/api/admin/jobs/stats");
    const retry = mockFetch(200, { id: 9, status: "pending" });
    await api.retryJob(9);
    const [url, init] = retry.mock.calls[0] as unknown as [string, RequestInit];
    expect([url, init.method]).toEqual(["/api/admin/jobs/9/retry", "POST"]);
  });
});

describe("email administration", () => {
  it("keeps the mail server's detail on a failed test so staff can fix the setting", async () => {
    mockFetch(502, { error: "EMAIL_SEND_FAILED", detail: "The mail server refused the message: 535 authentication failed" });
    const err = await api.sendTestEmail("a@example.com").catch(e => e);
    expect(err).toBeInstanceOf(ApiError);
    expect(err.code).toBe("EMAIL_SEND_FAILED");
    expect(err.detail).toContain("authentication failed");
  });
  it("lists the email log with a status filter and sends the test address as JSON", async () => {
    const list = mockFetch(200, { items: [], total: 0, page: 1, pageSize: 25 });
    await api.emailOutbox("failed", 2);
    expect((list.mock.calls[0] as unknown as [string])[0]).toBe("/api/admin/email/outbox?status=failed&page=2&pageSize=25");
    const test = mockFetch(200, { sent: true });
    await api.sendTestEmail("x@example.com");
    expect(JSON.parse((test.mock.calls[0] as unknown as [string, RequestInit])[1].body as string)).toEqual({ to: "x@example.com" });
  });
});

describe("ads administration", () => {
  it("uploads a picture as multipart with the admin CSRF header and no hand-made content type", async () => {
    const fn = mockFetch(201, { id: "a".repeat(32), url: "/api/files/" + "a".repeat(32) });
    const out = await api.uploadAdImage(new File([new Uint8Array([0xff, 0xd8, 0xff])], "banner.jpg", { type: "image/jpeg" }));
    const [url, init] = fn.mock.calls[0] as unknown as [string, RequestInit];
    expect([url, init.method]).toEqual(["/api/admin/ads/images", "POST"]);
    expect((init.headers as Record<string, string>)["X-Requested-With"]).toBe("sayserne-admin");
    expect(Object.keys(init.headers as Record<string, string>)).not.toContain("Content-Type");
    expect((init.body as FormData).get("file")).toBeInstanceOf(File);
    expect(out.id).toHaveLength(32);
  });
  it("creates or updates an ad depending on whether it has an id", async () => {
    const body = { title: "A", position: "sidebar", imageFileId: "x".repeat(32), linkUrl: "https://x.test", isActive: true };
    const created = mockFetch(201, { id: 1 });
    await api.saveAd(null, body);
    expect(((created.mock.calls[0] as unknown as [string, RequestInit])[1]).method).toBe("POST");
    const updated = mockFetch(200, { id: 1 });
    await api.saveAd(7, body);
    const [url, init] = updated.mock.calls[0] as unknown as [string, RequestInit];
    expect([url, init.method]).toEqual(["/api/admin/ads/7", "PUT"]);
  });
  it("books days, moves a batch, previews capacity and reads prices with the right addresses", async () => {
    const book = mockFetch(201, { id: 3 });
    await api.createAdBatch(5, "2026-11-01", "2026-11-03");
    expect([(book.mock.calls[0] as unknown as [string])[0], JSON.parse((book.mock.calls[0] as unknown as [string, RequestInit])[1].body as string)]).toEqual(["/api/admin/ads/5/batches", { from: "2026-11-01", to: "2026-11-03" }]);
    const pay = mockFetch(200, { id: 3 });
    await api.setAdBatchStatus(3, "paid");
    expect([(pay.mock.calls[0] as unknown as [string])[0], JSON.parse((pay.mock.calls[0] as unknown as [string, RequestInit])[1].body as string)]).toEqual(["/api/admin/ads/batches/3/status", { status: "paid" }]);
    const cap = mockFetch(200, []);
    await api.adCapacity(5, "2026-11-01", "2026-11-03");
    expect((cap.mock.calls[0] as unknown as [string])[0]).toBe("/api/admin/ads/5/capacity?from=2026-11-01&to=2026-11-03");
    const price = mockFetch(200, { dailyFee: 5, maxAds: 5 });
    await api.resolveAdPricing("home_banner", "LB", "Beirut");
    expect((price.mock.calls[0] as unknown as [string])[0]).toBe("/api/admin/ads/pricing/resolve?position=home_banner&country=LB&city=Beirut");
    const del = mockFetch(204);
    await api.deleteAdPricing(9);
    expect([(del.mock.calls[0] as unknown as [string])[0], ((del.mock.calls[0] as unknown as [string, RequestInit])[1]).method]).toEqual(["/api/admin/ads/pricing/9", "DELETE"]);
  });
});

describe("premium administration", () => {
  it("books days and moves a batch with the right addresses and bodies", async () => {
    const book = mockFetch(201, { id: 4 });
    await api.createPremiumBatch(12, "2026-12-01", "2026-12-05");
    expect([(book.mock.calls[0] as unknown as [string])[0], JSON.parse((book.mock.calls[0] as unknown as [string, RequestInit])[1].body as string)]).toEqual(["/api/admin/premium/batches", { professionId: 12, from: "2026-12-01", to: "2026-12-05" }]);
    const pay = mockFetch(200, { id: 4 });
    await api.setPremiumBatchStatus(4, "paid");
    expect([(pay.mock.calls[0] as unknown as [string])[0], JSON.parse((pay.mock.calls[0] as unknown as [string, RequestInit])[1].body as string)]).toEqual(["/api/admin/premium/batches/4/status", { status: "paid" }]);
  });
  it("finds listings, previews capacity and manages prices and limits", async () => {
    const find = mockFetch(200, []);
    await api.premiumFind("Nadia & Co");
    expect((find.mock.calls[0] as unknown as [string])[0]).toBe("/api/admin/premium/find?search=Nadia+%26+Co");
    const cap = mockFetch(200, []);
    await api.premiumCapacity("LB", "Beirut", "2026-12-01", "2026-12-05");
    expect((cap.mock.calls[0] as unknown as [string])[0]).toBe("/api/admin/premium/capacity?country=LB&city=Beirut&from=2026-12-01&to=2026-12-05");
    const save = mockFetch(200, { id: 1 });
    await api.savePremiumPricing({ countryCode: "LB", maxPremium: 3 });
    expect([(save.mock.calls[0] as unknown as [string])[0], ((save.mock.calls[0] as unknown as [string, RequestInit])[1]).method]).toEqual(["/api/admin/premium/pricing", "PUT"]);
    const del = mockFetch(204);
    await api.deletePremiumPricing(7);
    expect((del.mock.calls[0] as unknown as [string])[0]).toBe("/api/admin/premium/pricing/7");
  });
  it("sets only the featured flag: premium is not a flag any more", async () => {
    const fn = mockFetch(200, { user: { id: 3 } });
    await api.setFlags(3, true);
    expect(JSON.parse((fn.mock.calls[0] as unknown as [string, RequestInit])[1].body as string)).toEqual({ isStarred: true });
  });
});
