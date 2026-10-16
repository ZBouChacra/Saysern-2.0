// UI texts served by the API (/api/translations/<language>) and kept in the database, so an admin can reword them.
// The only text compiled into the app is what must work when the API cannot be reached.
let texts: Record<string, string> = {};
export const setTexts = (t: Record<string, string>) => { texts = t; };
export const getText = (key: string): string | undefined => texts[key];
export const NETWORK_ERROR = "Network error. Please try again.";
export const GENERIC_ERROR = "Something went wrong. Please try again.";
