// Typed client for the back-office API (/api/admin/*). Error texts come from the translations the database serves, not from this file.

export class ApiError extends Error {
  constructor(public readonly status: number, public readonly code: string, public readonly fieldErrors?: Record<string, string>, public readonly detail?: string) {
    super(code);
    this.name = "ApiError";
  }
}

async function request<T>(method: string, path: string, body?: unknown): Promise<T> {
  const response = await fetch(`/api${path}`, {
    method,
    credentials: "same-origin",
    headers: { "Content-Type": "application/json", "X-Requested-With": "sayserne-admin" },
    body: body === undefined ? undefined : JSON.stringify(body),
  });
  if (response.status === 204) return undefined as T;
  const text = await response.text();
  const data = text ? JSON.parse(text) : undefined;
  if (!response.ok) throw new ApiError(response.status, data?.error ?? (response.status === 429 ? "TooManyRequests" : response.status === 403 ? "FORBIDDEN" : "UNKNOWN"), data?.errors, data?.detail);
  return data as T;
}

const q = (params: Record<string, string | number | boolean | undefined | null>) => {
  const p = new URLSearchParams();
  for (const [k, v] of Object.entries(params)) if (v !== undefined && v !== null && v !== "") p.set(k, String(v));
  const s = p.toString();
  return s ? `?${s}` : "";
};

// ---- types
export interface UserDto { id: number; email: string; emailVerified: boolean; firstName: string; lastName: string; role: "user" | "admin"; profileType: string; isLocked: boolean; createdAtUtc: string; isPremium: boolean; isStarred: boolean }
export interface Session { user: UserDto; isSuper: boolean; permissions: string[]; recoveryCodesRemaining: number; idleMinutes: number }
export interface AdminUser extends UserDto { roleNames?: string; hasTwoFactor: boolean }
export interface Paged<T> { items: T[]; total: number; page: number; pageSize: number }
export interface Setting { key: string; group: string; type: "int" | "decimal" | "bool" | "string" | "secret"; isSet?: boolean; value: string; default: string; min?: number; max?: number; description: string; isPublic: boolean; updatedAt: string }
export interface LookupType { code: string; name: string; description?: string; parentType?: string; isSystem: boolean; valueCount: number }
export interface LookupValue { id: number; type: string; code: string; parentCode?: string; sortOrder: number; isActive: boolean; metadata?: unknown; names: Record<string, string> }
export interface TranslationRow { language: string; key: string; value: string; defaultValue: string; updatedAt: string }
export interface Permission { code: string; group: string; description: string }
export interface Role { id: number; name: string; description?: string; userCount: number; permissions: string[] }
export interface Category { id: number; name: string; nameAr?: string; description?: string; descriptionAr?: string; icon?: string; isBlocked: boolean }
export interface Service { id: number; categoryId: number; name: string; nameAr?: string; description?: string; descriptionAr?: string; isBlocked: boolean }
export interface AuditRow { id: number; occurredAt: string; actorId?: number; actorName?: string; action: string; entityType: string; entityId?: string; summary: string; details?: unknown; ip?: string }
export interface OutboxRow { id: number; userId?: number; to: string; eventCode: string; language: string; subject: string; status: "queued" | "sent" | "failed" | "skipped"; attempts: number; lastError?: string; createdAt: string; sentAt?: string }
export interface Ad { id: number; title: string; position: string; imageFileId: string; linkUrl: string; countryCode?: string; city?: string; isActive: boolean; feeOverride?: number; createdAt: string; runningToday: boolean }
export interface AdInput { title: string; position: string; imageFileId: string; linkUrl: string; countryCode?: string; city?: string; isActive: boolean; feeOverride?: number }
export interface AdBatch { id: number; adId: number; dateFrom: string; dateTo: string; feePerDay: number; totalFee: number; status: "pending" | "paid" | "cancelled"; createdAt: string; paidAt?: string; receiptId?: number; receiptNumber?: string }
/** Who paid and how. Marking a batch paid records this and issues a numbered receipt. */
export interface BillingInput { billedTo: string; billedDetails?: string; method: string; reference?: string; receivedOn?: string; notes?: string }
export interface PaymentRow { id: number; receiptNumber: string; kind: "ad" | "premium"; description: string; subtotal: number; vatAmount: number; total: number; currency: string; billedTo: string; method: string; reference?: string; receivedOn: string; status: "issued" | "void"; issuedAt: string }
export interface Receipt { id: number; seq: number; receiptNumber: string; kind: "ad" | "premium"; description: string; quantity: number; unitPrice: number; subtotal: number; vatPercent: number; vatAmount: number; total: number; currency: string; billedTo: string; billedDetails?: string; method: string; methodName?: string; reference?: string; receivedOn: string; notes?: string; status: "issued" | "void"; voidReason?: string; voidedAt?: string; issuedAt: string; companyName: string; companyAddress?: string; companyTaxId?: string; footer?: string }
export interface PaymentSummary { rows: { kind: string; method: string; receipts: number; subtotal: number; vat: number; total: number }[]; voided: number; total: number }
export interface AdPricing { id: number; position: string; countryCode?: string; city?: string; dailyFee?: number; maxAds?: number }
export interface ResolvedPricing { dailyFee: number; maxAds: number }
export interface AdStat { adId: number; title: string; position: string; impressions: number; uniqueImpressions: number; clicks: number; uniqueClicks: number }
export interface CapacityDay { day: string; booked: number; maxAds: number }
export interface PremiumBatch { id: number; professionId?: number; userId?: number; professionalName: string; serviceName: string; countryCode: string; city?: string; dateFrom: string; dateTo: string; entitledDays: number; daysAssigned: number; effectiveTo?: string; feePerDay: number; totalFee: number; status: "pending" | "paid" | "cancelled"; paidAt?: string; createdAt: string; activeToday: boolean; receiptId?: number; receiptNumber?: string }
export interface PremiumPricing { id: number; countryCode: string; city?: string; dailyFee?: number; maxPremium?: number }
export interface ResolvedPremiumPricing { dailyFee: number; maxPerCountry: number; maxPerCity?: number }
export interface PremiumCandidate { professionId: number; userId: number; firstName: string; lastName: string; serviceName: string; country?: string; city?: string; isPremium: boolean }
export interface PremiumCapacityDay { day: string; used: number; maxPremium: number; cityUsed?: number; maxCity?: number }
export interface AdminReview { id: number; rating: number; comment?: string; createdAt: string; replyText?: string; repliedAt?: string; isDeleted: boolean; deletedAt?: string; deleteReason?: string; reviewerId: number; reviewerName: string; professionalId: number; professionalName: string; deletedByName?: string }
export interface AdminChatRoom { id: number; userAId: number; userAName: string; userAEmail: string; userBId: number; userBName: string; userBEmail: string; createdAt: string; lastMessageAt: string; messageCount: number; deletedMessages: number; staffMessages: number; deletedByAAt?: string; deletedByBAt?: string }
export interface AdminChatMessage { id: number; roomId: number; senderId: number; senderName: string; messageType: string; content?: string; latitude?: number; longitude?: number; fileId?: string; isRead: boolean; isStaff: boolean; createdAt: string; deletedAt?: string; deletedBy?: number; deletedByName?: string }
export interface AdminChatThread { room: { id: number; userAId: number; userAName: string; userBId: number; userBName: string }; messages: AdminChatMessage[] }
export interface ContactRow { id: number; userId?: number; name: string; email: string; language: string; topic?: string; subject: string; preview: string; status: "new" | "in_progress" | "replied" | "archived" | "spam"; createdAt: string; repliedAt?: string }
export interface ContactMessage { id: number; userId?: number; name: string; email: string; language: string; topic?: string; topicName?: string; subject: string; body: string; status: ContactRow["status"]; replyBody?: string; repliedAt?: string; repliedByName?: string; internalNote?: string; createdAt: string }
export interface Dashboard {
  users?: { total: number; last7Days: number; last30Days: number; professionals: number; listings: number; locked: number; appointments: { pending: number; approved: number; completed: number; cancelled: number } };
  reviews?: { total: number; hidden: number; average: number };
  contact?: { unanswered: number; total: number };
  ads?: { runningToday: number; pendingBatches: number };
  premium?: { activeToday: number; pendingBatches: number };
  payments?: { receiptsLast30Days: number; receivedLast30Days: number; receivedThisMonth: number; voidLast30Days: number; currency: string };
  system?: { failedJobs?: number; pendingJobs?: number; failedEmails?: number; queuedEmails?: number };
  recentActivity?: { id: number; occurredAt: string; actorName?: string; action: string; summary: string }[];
}
export interface JobRow { id: number; type: string; payload?: unknown; status: "pending" | "running" | "done" | "failed"; attempts: number; maxAttempts: number; runAt: string; lastError?: string; createdAt: string; finishedAt?: string }
export interface JobStat { type: string; status: string; count: number }
export interface AuditFilter { from?: string; to?: string; actorId?: string; action?: string; entityType?: string; search?: string; page?: number; pageSize?: number }

/** Uploads an ad's picture (multipart). The server decides what the file really is, shrinks it and strips its metadata. */
async function uploadAdImage(file: File): Promise<{ id: string; url: string }> {
  const form = new FormData();
  form.append("file", file, file.name);
  const response = await fetch("/api/admin/ads/images", { method: "POST", credentials: "same-origin", headers: { "X-Requested-With": "sayserne-admin" }, body: form });
  const text = await response.text();
  const data = text ? JSON.parse(text) : undefined;
  if (!response.ok) throw new ApiError(response.status, data?.error ?? (response.status === 413 ? "FILE_TOO_LARGE" : "UNKNOWN"), data?.errors);
  return data as { id: string; url: string };
}

export const api = {
  // sign-in (password -> authenticator code or recovery code; first sign-in enrolls)
  login: (email: string, password: string) => request<{ status: "totp_required" | "enrollment_required" }>("POST", "/admin/auth/login", { email, password }),
  enroll: () => request<{ secret: string; otpauthUri: string }>("POST", "/admin/auth/2fa/enroll"),
  confirm: (code: string) => request<{ recoveryCodes: string[]; session: Session }>("POST", "/admin/auth/2fa/confirm", { code }),
  verify: (input: { code?: string; recoveryCode?: string }) => request<{ session: Session }>("POST", "/admin/auth/verify", input),
  me: () => request<Session>("GET", "/admin/auth/me"),
  logout: () => request<void>("POST", "/admin/auth/logout"),
  regenerateRecoveryCodes: (code: string) => request<{ recoveryCodes: string[] }>("POST", "/admin/auth/recovery-codes/regenerate", { code }),
  translations: (language = "en") => request<Record<string, string>>("GET", `/translations/${language}`),

  users: (search: string, page: number, pageSize = 20) => request<Paged<AdminUser>>("GET", `/admin/users${q({ search, page, pageSize })}`),
  lockUser: (id: number, isLocked: boolean) => request<{ user: UserDto }>("POST", `/admin/users/${id}/lock`, { isLocked }),
  setFlags: (id: number, isStarred: boolean) => request<{ user: UserDto }>("POST", `/admin/users/${id}/flags`, { isStarred }),
  setSuperAdmin: (id: number, role: "user" | "admin") => request<{ user: UserDto }>("POST", `/admin/users/${id}/role`, { role }),
  setUserRoles: (id: number, roleIds: number[]) => request<{ roles: { id: number; name: string }[] }>("PUT", `/admin/users/${id}/roles`, { roleIds }),
  resetTwoFactor: (id: number) => request<void>("POST", `/admin/users/${id}/two-factor/reset`),

  roles: () => request<Role[]>("GET", "/admin/roles"),
  permissions: () => request<Permission[]>("GET", "/admin/permissions"),
  saveRole: (id: number | null, role: { name: string; description?: string; permissions: string[] }) => id === null ? request<Role>("POST", "/admin/roles", role) : request<Role>("PUT", `/admin/roles/${id}`, role),
  deleteRole: (id: number) => request<void>("DELETE", `/admin/roles/${id}`),

  settings: () => request<Setting[]>("GET", "/admin/settings"),
  setSetting: (key: string, value: string) => request<Setting>("PUT", `/admin/settings/${encodeURIComponent(key)}`, { value }),
  resetSetting: (key: string) => request<Setting>("POST", `/admin/settings/${encodeURIComponent(key)}/reset`),

  lookupTypes: () => request<LookupType[]>("GET", "/admin/lookups/types"),
  saveLookupType: (code: string, t: { name: string; description?: string; parentType?: string }) => request<LookupType>("PUT", `/admin/lookups/types/${code}`, t),
  lookupValues: (type: string, parent?: string) => request<LookupValue[]>("GET", `/admin/lookups/${type}/values${q({ parent, includeInactive: true })}`),
  saveLookupValue: (type: string, code: string, v: { parentCode?: string; sortOrder?: number; isActive: boolean; metadata?: unknown; names: Record<string, string> }) =>
    request<LookupValue>("PUT", `/admin/lookups/${type}/values/${encodeURIComponent(code)}`, v),

  allTranslations: () => request<TranslationRow[]>("GET", "/admin/translations"),
  saveTranslation: (language: string, key: string, value: string) => request<TranslationRow>("PUT", `/admin/translations/${language}/${encodeURIComponent(key)}`, { value }),
  resetTranslation: (language: string, key: string) => request<TranslationRow>("POST", `/admin/translations/${language}/${encodeURIComponent(key)}/reset`),

  categories: () => request<Category[]>("GET", "/admin/catalog/categories"),
  services: (categoryId?: number) => request<Service[]>("GET", `/admin/catalog/services${q({ categoryId })}`),
  saveCategory: (id: number | null, c: Omit<Category, "id">) => id === null ? request<Category>("POST", "/admin/catalog/categories", c) : request<Category>("PUT", `/admin/catalog/categories/${id}`, c),
  saveService: (id: number | null, s: Omit<Service, "id">) => id === null ? request<Service>("POST", "/admin/catalog/services", s) : request<Service>("PUT", `/admin/catalog/services/${id}`, s),

  ads: (position?: string) => request<Ad[]>("GET", `/admin/ads${q({ position })}`),
  saveAd: (id: number | null, body: AdInput) => (id === null ? request<Ad>("POST", "/admin/ads", body) : request<Ad>("PUT", `/admin/ads/${id}`, body)),
  uploadAdImage,
  adBatches: (adId?: number) => request<AdBatch[]>("GET", `/admin/ads/batches${q({ adId })}`),
  createAdBatch: (adId: number, from: string, to: string) => request<AdBatch>("POST", `/admin/ads/${adId}/batches`, { from, to }),
  setAdBatchStatus: (id: number, status: "paid" | "cancelled", billing?: BillingInput) => request<AdBatch>("POST", `/admin/ads/batches/${id}/status`, { status, ...billing }),
  adCapacity: (adId: number, from: string, to: string) => request<CapacityDay[]>("GET", `/admin/ads/${adId}/capacity${q({ from, to })}`),
  adPricing: () => request<AdPricing[]>("GET", "/admin/ads/pricing"),
  saveAdPricing: (b: { position: string; countryCode?: string; city?: string; dailyFee?: number; maxAds?: number }) => request<AdPricing>("PUT", "/admin/ads/pricing", b),
  deleteAdPricing: (id: number) => request<void>("DELETE", `/admin/ads/pricing/${id}`),
  resolveAdPricing: (position: string, country?: string, city?: string) => request<ResolvedPricing>("GET", `/admin/ads/pricing/resolve${q({ position, country, city })}`),
  adStats: (from?: string, to?: string) => request<AdStat[]>("GET", `/admin/ads/stats${q({ from, to })}`),
  premiumBatches: (status?: string) => request<PremiumBatch[]>("GET", `/admin/premium/batches${q({ status })}`),
  premiumFind: (search: string) => request<PremiumCandidate[]>("GET", `/admin/premium/find${q({ search })}`),
  createPremiumBatch: (professionId: number, from: string, to: string) => request<PremiumBatch>("POST", "/admin/premium/batches", { professionId, from, to }),
  setPremiumBatchStatus: (id: number, status: "paid" | "cancelled", billing?: BillingInput) => request<PremiumBatch>("POST", `/admin/premium/batches/${id}/status`, { status, ...billing }),
  payments: (f: { status?: string; kind?: string; from?: string; to?: string; search?: string; page?: number }) => request<Paged<PaymentRow>>("GET", `/admin/payments${q({ ...f, pageSize: 25 })}`),
  paymentSummary: (from?: string, to?: string) => request<PaymentSummary>("GET", `/admin/payments/summary${q({ from, to })}`),
  receipt: (id: number) => request<Receipt>("GET", `/admin/payments/${id}`),
  updateReceiptBilling: (id: number, b: { billedTo: string; billedDetails?: string; notes?: string }) => request<Receipt>("PUT", `/admin/payments/${id}/billing`, b),
  premiumCapacity: (country: string, city: string | undefined, from: string, to: string) => request<PremiumCapacityDay[]>("GET", `/admin/premium/capacity${q({ country, city, from, to })}`),
  premiumPricing: () => request<PremiumPricing[]>("GET", "/admin/premium/pricing"),
  savePremiumPricing: (b: { countryCode: string; city?: string; dailyFee?: number; maxPremium?: number }) => request<PremiumPricing>("PUT", "/admin/premium/pricing", b),
  deletePremiumPricing: (id: number) => request<void>("DELETE", `/admin/premium/pricing/${id}`),
  resolvePremiumPricing: (country: string, city?: string) => request<ResolvedPremiumPricing>("GET", `/admin/premium/pricing/resolve${q({ country, city })}`),
  adminReviews: (status: string, search: string, page: number) => request<Paged<AdminReview>>("GET", `/admin/reviews${q({ status, search, page, pageSize: 25 })}`),
  hideReview: (id: number, reason: string) => request<AdminReview>("POST", `/admin/reviews/${id}/hide`, { reason }),
  restoreReview: (id: number) => request<AdminReview>("POST", `/admin/reviews/${id}/restore`),
  adminChats: (filter: string, search: string, page: number) => request<Paged<AdminChatRoom>>("GET", `/admin/chats${q({ filter, search, page, pageSize: 25 })}`),
  readChat: (roomId: number) => request<AdminChatThread>("GET", `/admin/chats/${roomId}/messages`),
  postStaffMessage: (roomId: number, content: string) => request<AdminChatMessage>("POST", `/admin/chats/${roomId}/messages`, { content }),
  contactList: (status: string, search: string, page: number) => request<Paged<ContactRow>>("GET", `/admin/contact${q({ status, search, page, pageSize: 25 })}`),
  contactGet: (id: number) => request<ContactMessage>("GET", `/admin/contact/${id}`),
  contactReply: (id: number, body: string) => request<ContactMessage>("POST", `/admin/contact/${id}/reply`, { body }),
  contactStatus: (id: number, status: string, note?: string) => request<ContactMessage>("POST", `/admin/contact/${id}/status`, { status, note }),
  dashboard: () => request<Dashboard>("GET", "/admin/dashboard"),
  emailOutbox: (status: string, page: number) => request<Paged<OutboxRow>>("GET", `/admin/email/outbox${q({ status, page, pageSize: 25 })}`),
  sendTestEmail: (to: string) => request<{ sent: boolean }>("POST", "/admin/email/test", { to }),
  jobs: (status: string, type: string, page: number) => request<Paged<JobRow>>("GET", `/admin/jobs${q({ status, type, page, pageSize: 25 })}`),
  jobStats: () => request<JobStat[]>("GET", "/admin/jobs/stats"),
  retryJob: (id: number) => request<JobRow>("POST", `/admin/jobs/${id}/retry`),
  audit: (f: AuditFilter) => request<Paged<AuditRow>>("GET", `/admin/audit${q({ ...f })}`),
};

let texts: Record<string, string> = {};
export const setTexts = (t: Record<string, string>) => { texts = t; };

/** A human message for an error. Uses the translated texts served by the API; unknown codes never leak raw internals. */
export function errorMessage(error: unknown): string {
  if (error instanceof ApiError) {
    return texts[`error.${error.code}`] ?? ADMIN_FALLBACK[error.code] ?? (error.status === 403 ? "You don't have permission to do that." : "Something went wrong. Please try again.");
  }
  return "Network error. Please try again.";
}

// Texts for conditions that only the back office can hit (the shared translations cover the public site).
const ADMIN_FALLBACK: Record<string, string> = {
  INVALID_TWO_FACTOR_CODE: "That code is not valid. Check your authenticator and try again.",
  PENDING_EXPIRED: "Your sign-in timed out. Start again.",
  ACCOUNT_LOCKED: "This account is locked for a while after too many failed attempts.",
  INVALID_CREDENTIALS: "Email or password is incorrect.",
  SETTING_OUT_OF_RANGE: "That value is outside the allowed range for this setting.",
  SETTING_INVALID: "That value is not valid for this setting.",
  SETTING_INCONSISTENT: "That value conflicts with a related setting.",
  DUPLICATE_ROLE: "A role with that name already exists.",
  DUPLICATE_NAME: "That name is already used.",
  ROLE_IN_USE: "This role is assigned to users. Remove it from them first.",
  INVALID_NAMES: "A name is required (English is mandatory).",
  INVALID_CODE: "Use letters, digits, dots, dashes or underscores only.",
  LOOKUP_PARENT_REQUIRED: "Choose the parent this value belongs to.",
  DEFAULT_LANGUAGE_REQUIRED: "The default language cannot be switched off.",
  CANNOT_MODIFY_SELF: "You cannot change your own account here.",
  LAST_ADMIN: "There must always be at least one active super admin.",
  INVALID_TEXT: "The text cannot be empty or longer than 4000 characters.",
  FORBIDDEN: "You don't have permission to do that.",
  UNAUTHENTICATED: "Please sign in.",
};
