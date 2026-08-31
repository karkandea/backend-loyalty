namespace BackendLoyalty.Application.Loyalty;

public sealed class AddStampRequest
{
    public int StampCount { get; init; } = 1;
    public string? TransactionNotes { get; init; }
}

public sealed record AddStampCommand(
    string BusinessId,
    string StaffId,
    string OutletId,
    string MemberId,
    string MemberCardId,
    int StampCount,
    string? TransactionNotes);

public sealed record AddStampResult(
    string TransactionId,
    IReadOnlyList<string> Transactions,
    string MemberCardId,
    int CurrentStamps,
    int RequiredStamps,
    bool DidAdvanceCard,
    int? ActiveCardLevel,
    IReadOnlyList<CardTransitionResult> CardTransitions,
    MilestoneHitResult? NewMilestone,
    IReadOnlyList<IssuedRewardTokenResult> NewRewards);

public sealed record CardTransitionResult(
    string FromCardId,
    string ToCardId,
    int? FromLevel,
    int? ToLevel,
    string Reason);

public sealed record MilestoneHitResult(
    int StampCount,
    string? RewardId);

public sealed record IssuedRewardTokenResult(
    string MemberRewardId,
    string Token,
    DateTime ExpiresAt);
