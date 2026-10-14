// Typed client for the ASP.NET Core API. The browser never talks to the database or to Google directly.

export type Role = "user" | "admin";

export interface User {
  id: number;
  email: string;
  emailVerified: boolean;
  firstName: string;
  lastName: string;
  role: Role;
  profileType: string;
  isLocked: boolean;
  createdAtUtc: string;
}

export interface Paged<T> {
  items: T[];
  total: number;
  page: number;
  pageSize: number;
}

export interface Providers {
  google: boolean;
  passwordRegistration: boolean;
}

export class ApiError extends Error {
  constructor(
    public readonly status: number,
    public readonly code: string,
    public readonly fieldErrors?: Record<string, string>,
  ) {
    super(code);
    this.name = "ApiError";
  }
}

async function request<T>(method: string, path: string, body?: unknown): Promise<T> {
  const response = await fetch(`/api${path}`, {
    method,
    credentials: "same-origin",
    headers: { "Content-Type": "application/json", "X-Requested-With": "sayserne-web" },
    body: body === undefined ? undefined : JSON.stringify(body),
  });
  if (response.status === 204) return undefined as T;
  const text = await response.text();
  const data = text ? JSON.parse(text) : undefined;
  if (!response.ok) throw new ApiError(response.status, data?.error ?? "UNKNOWN", data?.errors);
  return data as T;
}

export interface RegisterInput {
  email: string;
  password: string;
  firstName: string;
  lastName: string;
}

export const api = {
  providers: () => request<Providers>("GET", "/auth/providers"),
  me: () => request<{ user: User }>("GET", "/auth/me").then(r => r.user),
  register: (input: RegisterInput) => request<{ user: User }>("POST", "/auth/register", input).then(r => r.user),
  login: (email: string, password: string) => request<{ user: User }>("POST", "/auth/login", { email, password }).then(r => r.user),
  logout: () => request<void>("POST", "/auth/logout"),
  linkPending: (password: string) => request<{ user: User }>("POST", "/auth/google/link-pending", { password }).then(r => r.user),
  adminUsers: (search: string, page: number, pageSize = 20) =>
    request<Paged<User>>("GET", `/admin/users?search=${encodeURIComponent(search)}&page=${page}&pageSize=${pageSize}`),
  setLocked: (id: number, isLocked: boolean) => request<{ user: User }>("POST", `/admin/users/${id}/lock`, { isLocked }).then(r => r.user),
  setRole: (id: number, role: Role) => request<{ user: User }>("POST", `/admin/users/${id}/role`, { role }).then(r => r.user),
};

/** Where the browser navigates to start Google sign-in (a full-page redirect handled by the API). */
export const googleSignInUrl = (returnUrl = "/") => `/api/auth/google/start?returnUrl=${encodeURIComponent(returnUrl)}`;
export const googleLinkUrl = (returnUrl = "/") => `/api/auth/google/start?mode=link&returnUrl=${encodeURIComponent(returnUrl)}`;

const MESSAGES: Record<string, string> = {
  INVALID_CREDENTIALS: "Email or password is incorrect.",
  ACCOUNT_LOCKED: "This account is locked. Try again later or contact support.",
  DUPLICATE_EMAIL: "An account with this email already exists. Try signing in.",
  VALIDATION_FAILED: "Please fix the highlighted fields.",
  CSRF_REJECTED: "The request was blocked for your safety. Reload the page and try again.",
  LINK_EXPIRED: "The Google link request expired. Start Google sign-in again.",
  LINK_NOT_POSSIBLE: "This account has no password, so it can't be linked this way.",
  LAST_ADMIN: "There must always be at least one active admin.",
  CANNOT_MODIFY_SELF: "You can't change your own role or lock your own account.",
  USER_NOT_FOUND: "That user no longer exists.",
  INVALID_ROLE: "Unknown role.",
  GOOGLE_NOT_CONFIGURED: "Google sign-in isn't configured on this server.",
  SERVER_ERROR: "Something went wrong on our side. Please try again.",
  // redirect-style errors from the Google flow (?error=...)
  google_state: "The Google sign-in session expired or was invalid. Please try again.",
  google_denied: "Google sign-in was cancelled.",
  google_token: "Google sign-in could not be verified. Please try again.",
  google_exchange: "Could not complete Google sign-in. Please try again.",
  google_email_unverified: "Your Google email address isn't verified, so it can't be used to sign in.",
  google_conflict: "That sign-up collided with another one. Please try again.",
  account_locked: "This account is locked. Try again later or contact support.",
  login_required: "Please sign in first.",
};

export function errorMessage(error: unknown): string {
  const code = error instanceof ApiError ? error.code : typeof error === "string" ? error : "";
  return MESSAGES[code] ?? (code ? "Something went wrong. Please try again." : "Network error. Please try again.");
}
