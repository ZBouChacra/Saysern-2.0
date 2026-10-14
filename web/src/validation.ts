// Client-side checks that mirror the server's rules for fast feedback. The server remains the authority.

export function validateRegistration(v: { email: string; password: string; firstName: string; lastName: string }): Record<string, string> {
  const errors: Record<string, string> = {};
  const email = v.email.trim().toLowerCase();
  if (email.length === 0 || email.length > 254 || !/^[^@\s]+@[^@\s]+\.[^@\s]{2,}$/.test(email)) errors.email = "Enter a valid email address.";
  if (v.password.length < 10) errors.password = "Use at least 10 characters.";
  else if (v.password.length > 128) errors.password = "Use at most 128 characters.";
  else if (!/\p{L}/u.test(v.password) || !/\d/.test(v.password)) errors.password = "Include at least one letter and one digit.";
  else if (v.password.toLowerCase() === email) errors.password = "The password must not be your email address.";
  for (const [field, value] of [["firstName", v.firstName], ["lastName", v.lastName]] as const) {
    const t = value.trim();
    if (t.length === 0) errors[field] = "Required.";
    else if (t.length > 100) errors[field] = "At most 100 characters.";
  }
  return errors;
}
