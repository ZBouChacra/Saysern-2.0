// eslint-disable-next-line
import { GENERIC_ERROR, NETWORK_ERROR, getText } from "./texts";
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

/** Uploads one file (multipart). The server decides what it really is, shrinks it, and answers 201 (image, ready) or 202 (video, being compressed). */
async function upload(file: File, purpose: UploadPurpose, roomId?: number): Promise<UploadResult> {
  const form = new FormData();
  form.append("file", file, file.name);
  const response = await fetch(`/api/files?purpose=${encodeURIComponent(purpose)}${roomId === undefined ? "" : `&roomId=${roomId}`}`, {
    method: "POST", credentials: "same-origin", headers: { "X-Requested-With": "sayserne-web" }, body: form,   // no Content-Type: the browser adds the multipart boundary
  });
  const text = await response.text();
  const data = text ? JSON.parse(text) : undefined;
  if (!response.ok) throw new ApiError(response.status, data?.error ?? (response.status === 413 ? "FILE_TOO_LARGE" : response.status === 429 ? "TooManyRequests" : "UNKNOWN"), data?.errors);
  return data as UploadResult;
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
export interface ChatMessage { id: number; roomId: number; senderId: number; messageType: "text" | "location" | "image" | "video"; content?: string; latitude?: number; longitude?: number; fileId?: string; isRead: boolean; createdAt: string }

export interface ServedAd { id: number; title: string; position: string; imageUrl: string; clickUrl: string }
export interface NotificationPref { event: string; channel: string; enabled: boolean }

export type UploadPurpose = "profile_photo" | "banner_photo" | "chat_media";
export interface UploadResult { id: string; status: "ready" | "processing"; kind: "image" | "video"; url: string; width?: number; height?: number; bytes?: number }
export interface FileStatus { id: string; status: "processing" | "ready" | "failed"; kind: "image" | "video"; failureCode?: string; url?: string; width?: number; height?: number }
export interface Alert { id: number; name: string; searchCriteria: SearchFilters; frequency: string; createdAt: string }

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
  verifyEmail: (token: string) => request<{ verified: boolean }>("POST", "/auth/verify-email", { token }),
  resendVerification: () => request<{ sent?: boolean; alreadyVerified?: boolean }>("POST", "/auth/resend-verification"),
  forgotPassword: (email: string) => request<{ accepted: boolean }>("POST", "/auth/forgot-password", { email }),
  resetPassword: (token: string, password: string) => request<{ reset: boolean }>("POST", "/auth/reset-password", { token, password }),
  notificationPrefs: () => request<NotificationPref[]>("GET", "/me/notifications"),
  setNotificationPref: (event: string, channel: string, enabled: boolean) => request<NotificationPref[]>("PUT", "/me/notifications", { event, channel, enabled }),
  ads: (position: string, country?: string, city?: string) => request<ServedAd[]>("GET", `/ads?position=${encodeURIComponent(position)}${country ? `&country=${encodeURIComponent(country)}` : ""}${city ? `&city=${encodeURIComponent(city)}` : ""}`),
  /** Fire and forget: failing to count an impression must never disturb the page. */
  adImpression: (id: number): void => { void fetch(`/api/ads/${id}/impression`, { method: "POST", credentials: "same-origin", keepalive: true, headers: { "X-Requested-With": "sayserne-web" } }).catch(() => undefined); },
  uploadFile: upload,
  fileStatus: (id: string) => request<FileStatus>("GET", `/files/${id}/status`),
  rooms: () => request<ChatRoomSummary[]>("GET", "/chat/rooms"),
  startChat: (userId: number) => request<{ id: number }>("POST", "/chat/rooms", { userId }),
  messages: (roomId: number) => request<ChatMessage[]>("GET", `/chat/rooms/${roomId}/messages?limit=100`),
  send: (roomId: number, m: { type?: "text" | "location" | "image" | "video"; content?: string; latitude?: number; longitude?: number; fileId?: string }) => request<ChatMessage>("POST", `/chat/rooms/${roomId}/messages`, m),
  unread: () => request<{ unread: number }>("GET", "/chat/unread-count").then(r => r.unread),
  alerts: () => request<Alert[]>("GET", "/alerts"),
  createAlert: (name: string, searchCriteria: SearchFilters, frequency: Alert["frequency"]) => request<Alert>("POST", "/alerts", { name, searchCriteria, frequency }),
  deleteAlert: (id: number) => request<void>("DELETE", `/alerts/${id}`),
};

/** Where the browser navigates to start Google sign-in (a full-page redirect handled by the API). */
export const googleSignInUrl = (returnUrl = "/") => `/api/auth/google/start?returnUrl=${encodeURIComponent(returnUrl)}`;
export const googleLinkUrl = (returnUrl = "/") => `/api/auth/google/start?mode=link&returnUrl=${encodeURIComponent(returnUrl)}`;

/** A human message for an error code, from the texts the API serves (see texts.ts). Unknown codes never leak raw internals. */
export function errorMessage(error: unknown): string {
  const code = error instanceof ApiError ? error.code : typeof error === "string" ? error : "";
  return (code && getText(`error.${code}`)) || (code ? GENERIC_ERROR : NETWORK_ERROR);
}
