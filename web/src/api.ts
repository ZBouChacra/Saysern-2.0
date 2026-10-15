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
  isPremium: boolean;
  isStarred: boolean;
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
  if (!response.ok) throw new ApiError(response.status, data?.error ?? (response.status === 429 ? "TooManyRequests" : "UNKNOWN"), data?.errors);
  return data as T;
}

// ------------------------------------------------------------------ marketplace types
export interface Category { id: number; name: string; nameAr?: string; icon?: string; isBlocked: boolean }
export interface Service { id: number; categoryId: number; name: string; nameAr?: string; isBlocked: boolean }

export interface Listing {
  userId: number; professionId: number; categoryId: number; serviceId: number; categoryName: string; serviceName: string;
  costPerHour?: number; yearsOfExperience: number; avgRating: number; totalReviews: number; hasTeam: boolean; teamSize: number;
  geographicAreas: string[]; firstName: string; lastName: string; profilePhoto?: string; sex?: string; nationality?: string; age?: number;
  isPremium: boolean; isStarred: boolean; country?: string; city?: string; hasOffice: boolean; officeCity?: string; officeCountry?: string;
}

export interface SearchFilters {
  categoryId?: number; serviceId?: number; firstName?: string; lastName?: string; sex?: string; minAge?: number; maxAge?: number;
  nationality?: string; country?: string; city?: string; hasOffice?: boolean; minStars?: number; minCost?: number; maxCost?: number;
  minExperience?: number; maxExperience?: number; hasTeam?: boolean; page?: number; pageSize?: number;
}

export interface PrivateProfile {
  id: number; email: string; emailVerified: boolean; firstName: string; lastName: string; phone?: string; sex?: string; dateOfBirth?: string;
  nationality?: string; country?: string; city?: string; profilePhoto?: string; bannerPhoto?: string; bio?: string; portfolio?: string;
  preferredLanguage: "en" | "ar"; profileType: "customer" | "professional"; role: Role; isPremium: boolean; isStarred: boolean;
}
export type ProfileInput = Omit<PrivateProfile, "id" | "email" | "emailVerified" | "role" | "isPremium" | "isStarred">;

export interface PublicProfile {
  id: number; firstName: string; lastName: string; sex?: string; nationality?: string; country?: string; city?: string;
  profilePhoto?: string; bannerPhoto?: string; bio?: string; portfolio?: string; age?: number; isPremium: boolean; isStarred: boolean;
}

export interface Profession {
  id: number; userId: number; categoryId: number; serviceId: number; categoryName: string; serviceName: string; country?: string; city?: string;
  costPerHour?: number; yearsOfExperience: number; website?: string; hasTeam: boolean; teamSize: number; hasOffice: boolean; officeAddress?: string;
  officeCity?: string; officeCountry?: string; geographicAreas: string[]; avgRating: number; totalReviews: number; isHidden: boolean; isLocked: boolean;
}
export interface ProfessionInput {
  categoryId?: number; serviceId?: number; country?: string; city?: string; costPerHour?: number | null; yearsOfExperience?: number; website?: string;
  hasTeam?: boolean; teamSize?: number; hasOffice?: boolean; officeAddress?: string; officeCity?: string; officeCountry?: string;
  geographicAreas?: string[]; isHidden?: boolean;
}
export interface Slot { professionId?: number; dayOfWeek: number; startTime: string; endTime: string }
export interface ProfessionalPage { profile: PublicProfile; professions: Profession[]; availability: Slot[] }
export interface BusySlot { startsAt: string; endsAt: string }

export type AppointmentStatus = "pending" | "approved" | "cancelled" | "completed";
export interface Appointment {
  id: number; clientId: number; professionalId: number; professionId?: number; startsAt: string; endsAt: string; durationMinutes: number;
  description?: string; status: AppointmentStatus; createdAt: string; clientFirstName: string; clientLastName: string;
  professionalFirstName: string; professionalLastName: string; serviceName?: string; categoryName?: string; hasReview: boolean;
}
export interface ReviewItem { id: number; rating: number; comment?: string; createdAt: string; reviewerFirstName: string; reviewerLastName: string }
export interface Eligible { id: number; startsAt: string }

export interface ChatRoomSummary {
  id: number; otherUserId: number; otherFirstName: string; otherLastName: string; lastMessageAt: string; lastMessageType?: string;
  lastMessageContent?: string; lastMessageSenderId?: number; unreadCount: number;
}
export interface ChatMessage { id: number; roomId: number; senderId: number; messageType: "text" | "location"; content?: string; latitude?: number; longitude?: number; isRead: boolean; createdAt: string }
export interface Alert { id: number; name: string; searchCriteria: SearchFilters; frequency: "daily" | "weekly" | "monthly"; createdAt: string }

/** Query string for the search API and the search page URL; empty values are skipped. */
export function toQuery(filters: SearchFilters): string {
  const q = new URLSearchParams();
  for (const [k, v] of Object.entries(filters)) if (v !== undefined && v !== null && v !== "") q.set(k, String(v));
  return q.toString();
}

/** Reverse of toQuery: URL parameters to typed filters (unknown or malformed values are dropped). */
export function fromQuery(params: URLSearchParams): SearchFilters {
  const num = (k: string) => { const v = params.get(k); const n = v === null || v === "" ? NaN : Number(v); return Number.isFinite(n) ? n : undefined; };
  const str = (k: string) => params.get(k)?.trim() || undefined;
  const bool = (k: string) => (params.get(k) === "true" ? true : params.get(k) === "false" ? false : undefined);
  return {
    categoryId: num("categoryId"), serviceId: num("serviceId"), firstName: str("firstName"), lastName: str("lastName"), sex: str("sex"),
    minAge: num("minAge"), maxAge: num("maxAge"), nationality: str("nationality"), country: str("country"), city: str("city"),
    hasOffice: bool("hasOffice"), minStars: num("minStars"), minCost: num("minCost"), maxCost: num("maxCost"),
    minExperience: num("minExperience"), maxExperience: num("maxExperience"), hasTeam: bool("hasTeam"), page: num("page"), pageSize: num("pageSize"),
  };
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
  categories: () => request<Category[]>("GET", "/catalog/categories"),
  services: (categoryId?: number) => request<Service[]>("GET", `/catalog/services${categoryId ? `?categoryId=${categoryId}` : ""}`),
  topProfessionals: (limit = 6) => request<Listing[]>("GET", `/professionals/top?limit=${limit}`),
  search: (f: SearchFilters) => request<Paged<Listing>>("GET", `/professionals/search?${toQuery(f)}`),
  professional: (id: number) => request<ProfessionalPage>("GET", `/professionals/${id}`),
  busy: (id: number) => request<BusySlot[]>("GET", `/professionals/${id}/busy`),
  reviews: (id: number) => request<ReviewItem[]>("GET", `/professionals/${id}/reviews`),
  myProfile: () => request<PrivateProfile>("GET", "/me/profile"),
  saveProfile: (p: ProfileInput) => request<PrivateProfile>("PUT", "/me/profile", p),
  myProfessions: () => request<Profession[]>("GET", "/me/professions"),
  addProfession: (p: ProfessionInput) => request<Profession>("POST", "/me/professions", p),
  updateProfession: (id: number, p: ProfessionInput) => request<Profession>("PUT", `/me/professions/${id}`, p),
  deleteProfession: (id: number) => request<void>("DELETE", `/me/professions/${id}`),
  myAvailability: () => request<Slot[]>("GET", "/me/availability"),
  saveAvailability: (professionId: number | null, slots: Slot[]) => request<Slot[]>("PUT", "/me/availability", { professionId, slots }),
  book: (b: { professionalId: number; professionId?: number; startsAt: string; durationMinutes: number; description?: string }) => request<Appointment>("POST", "/appointments", b),
  appointments: () => request<Appointment[]>("GET", "/appointments"),
  setStatus: (id: number, status: "approved" | "cancelled" | "completed") => request<Appointment>("POST", `/appointments/${id}/status`, { status }),
  eligible: (professionalId: number) => request<Eligible[]>("GET", `/reviews/eligible?professionalId=${professionalId}`),
  review: (appointmentId: number, rating: number, comment?: string) => request<{ id: number }>("POST", "/reviews", { appointmentId, rating, comment }),
  rooms: () => request<ChatRoomSummary[]>("GET", "/chat/rooms"),
  startChat: (userId: number) => request<{ id: number }>("POST", "/chat/rooms", { userId }),
  messages: (roomId: number) => request<ChatMessage[]>("GET", `/chat/rooms/${roomId}/messages?limit=100`),
  send: (roomId: number, m: { type?: "text" | "location"; content?: string; latitude?: number; longitude?: number }) => request<ChatMessage>("POST", `/chat/rooms/${roomId}/messages`, m),
  unread: () => request<{ unread: number }>("GET", "/chat/unread-count").then(r => r.unread),
  alerts: () => request<Alert[]>("GET", "/alerts"),
  createAlert: (name: string, searchCriteria: SearchFilters, frequency: Alert["frequency"]) => request<Alert>("POST", "/alerts", { name, searchCriteria, frequency }),
  deleteAlert: (id: number) => request<void>("DELETE", `/alerts/${id}`),
  setFlags: (id: number, isPremium: boolean, isStarred: boolean) => request<{ user: User }>("POST", `/admin/users/${id}/flags`, { isPremium, isStarred }).then(r => r.user),
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
  SLOT_TAKEN: "That time is no longer available. Please choose another.",
  START_IN_PAST: "Choose a time in the future.",
  CANNOT_BOOK_SELF: "You can't book yourself.",
  PROFESSIONAL_NOT_FOUND: "That professional is not available for booking.",
  PROFESSION_NOT_FOUND: "That service listing no longer exists.",
  INVALID_DURATION: "Choose a duration between 15 minutes and 8 hours.",
  INVALID_TRANSITION: "That action isn't possible for this appointment any more.",
  FORBIDDEN_ACTION: "Only the professional can do that.",
  NOT_STARTED_YET: "You can complete an appointment once it has started.",
  NOT_COMPLETED: "Only completed appointments can be reviewed.",
  REVIEW_EXISTS: "You already reviewed this appointment.",
  APPOINTMENT_NOT_FOUND: "That appointment doesn't exist.",
  NOT_A_PROFESSIONAL: "Switch your profile type to professional first.",
  HIDE_PROFESSIONS_FIRST: "Hide your service listings before switching back to a customer profile.",
  DUPLICATE_PROFESSION: "You already offer that service.",
  INVALID_SERVICE: "That service doesn't belong to the chosen category.",
  ROOM_NOT_FOUND: "That conversation doesn't exist.",
  CANNOT_CHAT_SELF: "You can't message yourself.",
  ALERT_LIMIT: "You can save at most 20 searches.",
  TooManyRequests: "You're doing that too quickly. Please wait a moment.",
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
