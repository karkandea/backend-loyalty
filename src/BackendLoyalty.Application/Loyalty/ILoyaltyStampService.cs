namespace BackendLoyalty.Application.Loyalty;

public interface ILoyaltyStampService
{
    Task<AddStampResult> AddStampsAsync(AddStampCommand command, CancellationToken cancellationToken = default);
}
