namespace BackendLoyalty.Application.Rewards;

public enum RewardRedemptionErrorCode
{
    NotFound,
    Forbidden,
    Used,
    Expired,
    Unavailable,
    MissingMemberCard,
    Internal
}

public sealed class RewardRedemptionException(
    RewardRedemptionErrorCode code,
    string message,
    Exception? innerException = null) : Exception(message, innerException)
{
    public RewardRedemptionErrorCode Code { get; } = code;
}
