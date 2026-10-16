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
export interface JobRow { id: number; type: string; payload?: unknown; status: "pending" | "running" | "done" | "failed"; attempts: number; maxAttempts: number; runAt: string; lastError?: string; createdAt: string; finishedAt?: string }
export interface JobStat { type: string; status: string; count: number }
export interface AuditFilter { from?: string; to?: string; actorId?: string; action?: string; entityType?: string; search?: string; page?: number; pageSize?: number }

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
  setFlags: (id: number, isPremium: boolean, isStarred: boolean) => request<{ user: UserDto }>("POST", `/admin/users/${id}/flags`, { isPremium, isStarred }),
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
