using System.Text.RegularExpressions;

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
    public static Dictionary<string, string> ValidateRegistration(RegisterRequest r)
    {
        var errors = new Dictionary<string, string>();
        var email = NormalizeEmail(r.Email);
        if (!IsValidEmail(email)) errors["email"] = "Enter a valid email address.";

        var password = r.Password ?? string.Empty;
        if (password.Length < 10) errors["password"] = "Use at least 10 characters.";
        else if (password.Length > 128) errors["password"] = "Use at most 128 characters.";
        else if (!password.Any(char.IsLetter) || !password.Any(char.IsDigit)) errors["password"] = "Include at least one letter and one digit.";
        else if (string.Equals(password, email, StringComparison.OrdinalIgnoreCase)) errors["password"] = "The password must not be your email address.";

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
