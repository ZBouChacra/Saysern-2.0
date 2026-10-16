// Client-side checks that mirror the server's rules for fast feedback. The server remains the authority.

export interface PasswordRules { minLength: number; requireLetter: boolean; requireDigit: boolean }

export function validateRegistration(v: { email: string; password: string; firstName: string; lastName: string }, rules: PasswordRules): Record<string, string> {
  const errors: Record<string, string> = {};
  const email = v.email.trim().toLowerCase();
  if (email.length === 0 || email.length > 254 || !/^[^@\s]+@[^@\s]+\.[^@\s]{2,}$/.test(email)) errors.email = "Enter a valid email address.";
  if (v.password.length < rules.minLength) errors.password = `Use at least ${rules.minLength} characters.`;
  else if (v.password.length > 128) errors.password = "Use at most 128 characters.";
  else if (rules.requireLetter && !/\p{L}/u.test(v.password)) errors.password = "Include at least one letter.";
  else if (rules.requireDigit && !/\d/.test(v.password)) errors.password = "Include at least one digit.";
  else if (v.password.toLowerCase() === email) errors.password = "The password must not be your email address.";
  for (const [field, value] of [["firstName", v.firstName], ["lastName", v.lastName]] as const) {
    const t = value.trim();
    if (t.length === 0) errors[field] = "Required.";
    else if (t.length > 100) errors[field] = "At most 100 characters.";
  }
  return errors;
}
