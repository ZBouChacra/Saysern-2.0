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
            or "NOT_COMPLETED" or "HIDE_PROFESSIONS_FIRST" or "ALERT_LIMIT" or "NOT_A_PROFESSIONAL"
            or "DUPLICATE_ROLE" or "ROLE_IN_USE" or "DUPLICATE_NAME" or "SETTING_INCONSISTENT" or "DEFAULT_LANGUAGE_REQUIRED"
            or "TWO_FACTOR_ALREADY_ENROLLED" or "TWO_FACTOR_NOT_STARTED" or "FILE_NOT_READY" or "JOB_NOT_RETRYABLE" => 409,
        "USER_NOT_FOUND" or "PROFESSION_NOT_FOUND" or "PROFESSIONAL_NOT_FOUND" or "APPOINTMENT_NOT_FOUND"
            or "ROOM_NOT_FOUND" or "ALERT_NOT_FOUND" or "NOT_FOUND" or "SETTING_NOT_FOUND" or "LOOKUP_TYPE_NOT_FOUND" or "ROLE_NOT_FOUND"
            or "TRANSLATION_NOT_FOUND" or "CATEGORY_NOT_FOUND" or "SERVICE_NOT_FOUND" or "FILE_NOT_FOUND" => 404,
        "FILE_TOO_LARGE" => 413,
        "AD_NOT_FOUND" or "PRICING_NOT_FOUND" or "BATCH_NOT_FOUND" => 404,
        "AD_CAPACITY_EXCEEDED" or "AD_INACTIVE" => 409,
        "STORAGE_UNAVAILABLE" => 503,
        "FORBIDDEN_ACTION" => 403,
        "INVALID_CREDENTIALS" or "UNAUTHENTICATED" or "INVALID_TWO_FACTOR_CODE" or "TOTP_REPLAY" or "INVALID_RECOVERY_CODE" or "PENDING_EXPIRED" => 401,
        "ACCOUNT_LOCKED" or "EMAIL_NOT_VERIFIED" => 403,
        "TOO_MANY_EMAILS" => 429,
        "EMAIL_NOT_CONFIGURED" => 409,
        _ => 400,
    };
}
