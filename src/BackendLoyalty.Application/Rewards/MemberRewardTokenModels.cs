namespace BackendLoyalty.Application.Rewards;

public sealed record IssueMemberRewardTokenCommand(
    string BusinessId,
    string MemberId,
    string MemberRewardId);

public sealed record MemberRewardTokenResult(
    RewardTokenResult Token,
    long ExpiresAtMs);

public sealed record RewardTokenResult(
    string Id,
    string Token,
    string Status,
    DateTime ExpiresAt);
