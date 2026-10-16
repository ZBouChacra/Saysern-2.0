using System.Text.RegularExpressions;

using Sayserne.Api.Platform;

namespace Sayserne.Api.Auth;

public sealed record RegisterRequest(string? Email, string? Password, string? FirstName, string? LastName);
public sealed record LoginRequest(string? Email, string? Password);

public static partial class InputValidation
{
    [GeneratedRegex(@"^[^@\s]+@[^@\s]+\.[^@\s]{2,}$")]
    private static partial Regex EmailPattern();

    public static string NormalizeEmail(string? email) => (email ?? string.Empty).Trim().ToLowerInvariant();

    public static bool IsValidEmail(string normalizedEmail) =>
        normalizedEmail.Length is > 0 and <= 254 && EmailPattern().IsMatch(normalizedEmail);

    /// <summary>Returns field -> message; empty when the request is valid.</summary>
    /// <summary>Why a password is not acceptable under the current settings (null = fine). Used by registration and by password reset.</summary>
    public static string? PasswordProblem(string password, string? email, ISettingsService settings)
    {
        var minLength = settings.GetInt("Auth.PasswordMinLength");
        if (password.Length < minLength) return $"Use at least {minLength} characters.";
        if (password.Length > 128) return "Use at most 128 characters.";
        if (settings.GetBool("Auth.PasswordRequireLetter") && !password.Any(char.IsLetter)) return "Include at least one letter.";
        if (settings.GetBool("Auth.PasswordRequireDigit") && !password.Any(char.IsDigit)) return "Include at least one digit.";
        if (email is not null && string.Equals(password, email, StringComparison.OrdinalIgnoreCase)) return "The password must not be your email address.";
        return null;
    }

    public static Dictionary<string, string> ValidateRegistration(RegisterRequest r, ISettingsService settings)
    {
        var errors = new Dictionary<string, string>();
        var email = NormalizeEmail(r.Email);
        if (!IsValidEmail(email)) errors["email"] = "Enter a valid email address.";

        if (PasswordProblem(r.Password ?? string.Empty, email, settings) is { } problem) errors["password"] = problem;

        foreach (var (field, value) in new[] { ("firstName", r.FirstName), ("lastName", r.LastName) })
        {
            var v = (value ?? string.Empty).Trim();
            if (v.Length == 0) errors[field] = "Required.";
            else if (v.Length > 100) errors[field] = "At most 100 characters.";
            else if (v.Any(char.IsControl)) errors[field] = "Contains invalid characters.";
        }
        return errors;
    }

    /// <summary>Only same-site relative paths may be used as a post-login redirect (blocks open redirects).</summary>
    public static string SafeReturnUrl(string? url) =>
        url is { Length: > 0 and <= 200 } && url[0] == '/' && !url.StartsWith("//", StringComparison.Ordinal)
        && !url.Contains('\\') && !url.Any(char.IsControl) ? url : "/";
}
