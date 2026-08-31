namespace BackendLoyalty.Api.Contracts;

public sealed record ApiError(string Code, string Message, object? Details = null);

public sealed record ApiResponse<T>(
    bool Success,
    T? Data,
    ApiError? Error,
    DateTime Timestamp)
{
    public static ApiResponse<T> Ok(T data) => new(true, data, null, DateTime.UtcNow);

    public static ApiResponse<T> Fail(string code, string message, object? details = null) =>
        new(false, default, new ApiError(code, message, details), DateTime.UtcNow);
}
