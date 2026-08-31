namespace BackendLoyalty.Application.Rewards;

public sealed class RewardScanRequest
{
    public string RewardToken { get; init; } = string.Empty;
}

public sealed record RedeemRewardCommand(
    string BusinessId,
    string StaffId,
    string OutletId,
    string RewardToken);

public sealed record RewardRedemptionResult(
    string RewardTokenId,
    string MemberRewardId,
    string Status,
    DateTime RedeemedAt,
    string? TransactionId);
