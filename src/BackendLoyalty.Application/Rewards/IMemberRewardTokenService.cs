namespace BackendLoyalty.Application.Rewards;

public interface IMemberRewardTokenService
{
    Task<MemberRewardTokenResult> IssueAsync(
        IssueMemberRewardTokenCommand command,
        CancellationToken cancellationToken = default);
}
