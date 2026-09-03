namespace BackendLoyalty.Application.Rewards;

public interface IRewardRedemptionService
{
    Task<RewardRedemptionExecutionResult> RedeemAsync(
        RedeemRewardCommand command,
        CancellationToken cancellationToken = default);
}
