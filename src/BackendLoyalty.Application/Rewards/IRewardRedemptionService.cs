namespace BackendLoyalty.Application.Rewards;

public interface IRewardRedemptionService
{
    Task<RewardRedemptionResult> RedeemAsync(
        RedeemRewardCommand command,
        CancellationToken cancellationToken = default);
}
