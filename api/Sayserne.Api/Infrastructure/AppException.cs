namespace Sayserne.Api;

/// <summary>A business/validation error with a stable machine-readable code.</summary>
public sealed class AppException(string code, int status = 0, string? message = null, IDictionary<string, string>? fieldErrors = null)
    : Exception(message ?? code)
{
    public string Code { get; } = code;
    public int Status { get; } = status != 0 ? status : ErrorCodes.StatusFor(code);
    public IDictionary<string, string>? FieldErrors { get; } = fieldErrors;
}

public static class ErrorCodes
{
    // Codes raised by stored procedures (SIGNAL ... MESSAGE_TEXT) are mapped to HTTP statuses here.
    public static int StatusFor(string code) => code switch
    {
        "DUPLICATE_EMAIL" or "DUPLICATE_ACCOUNT" or "IDENTITY_ALREADY_LINKED" or "LAST_ADMIN" => 409,
        "USER_NOT_FOUND" => 404,
        "INVALID_CREDENTIALS" => 401,
        "ACCOUNT_LOCKED" => 403,
        _ => 400,
    };
}
