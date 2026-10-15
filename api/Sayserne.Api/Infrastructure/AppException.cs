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
        "DUPLICATE_EMAIL" or "DUPLICATE_ACCOUNT" or "IDENTITY_ALREADY_LINKED" or "LAST_ADMIN"
            or "SLOT_TAKEN" or "DUPLICATE_PROFESSION" or "REVIEW_EXISTS" or "INVALID_TRANSITION" or "NOT_STARTED_YET"
            or "NOT_COMPLETED" or "HIDE_PROFESSIONS_FIRST" or "ALERT_LIMIT" or "NOT_A_PROFESSIONAL" => 409,
        "USER_NOT_FOUND" or "PROFESSION_NOT_FOUND" or "PROFESSIONAL_NOT_FOUND" or "APPOINTMENT_NOT_FOUND"
            or "ROOM_NOT_FOUND" or "ALERT_NOT_FOUND" or "NOT_FOUND" => 404,
        "FORBIDDEN_ACTION" => 403,
        "INVALID_CREDENTIALS" => 401,
        "ACCOUNT_LOCKED" => 403,
        _ => 400,
    };
}
