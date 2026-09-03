namespace BackendLoyalty.Application.Loyalty;

public enum LoyaltyOperationErrorCode
{
    Validation,
    NotFound,
    Internal
}

public sealed class LoyaltyOperationException(
    LoyaltyOperationErrorCode code,
    string message,
    Exception? innerException = null) : Exception(message, innerException)
{
    public LoyaltyOperationErrorCode Code { get; } = code;
}
