namespace BackendLoyalty.Application.Rewards;

public enum MemberRewardTokenErrorCode
{
    NotFound,
    Unavailable,
    Internal
}

public sealed class MemberRewardTokenException(
    MemberRewardTokenErrorCode code,
    string message,
    Exception? innerException = null) : Exception(message, innerException)
{
    public MemberRewardTokenErrorCode Code { get; } = code;
}
