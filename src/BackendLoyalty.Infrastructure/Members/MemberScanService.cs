using BackendLoyalty.Application.Members;
using BackendLoyalty.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace BackendLoyalty.Infrastructure.Members;

public sealed class MemberScanService(LoyaltyDbContext dbContext) : IMemberScanService
{
    public async Task<MemberScanResult?> ScanAsync(
        string businessId,
        string memberBarcode,
        CancellationToken cancellationToken = default)
    {
        var member = await dbContext.Members
            .AsNoTracking()
            .Where(x => x.BusinessId == businessId && x.MemberBarcode == memberBarcode)
            .Select(x => new { x.Id, x.Name, x.Email })
            .SingleOrDefaultAsync(cancellationToken);

        if (member is null)
        {
            return null;
        }

        var activeMemberCard = await dbContext.MemberCards
            .AsNoTracking()
            .Where(x => x.BusinessId == businessId && x.MemberId == member.Id && x.IsActive)
            .OrderByDescending(x => x.StartedAt)
            .Select(x => new { x.Id, x.CardId, x.CurrentStamps })
            .FirstOrDefaultAsync(cancellationToken);

        ActiveMemberCardResult? activeCardResult = null;

        if (activeMemberCard is not null)
        {
            var card = await dbContext.Cards
                .AsNoTracking()
                .Where(x => x.BusinessId == businessId && x.Id == activeMemberCard.CardId)
                .Select(x => new { x.Name, x.RequiredStamps })
                .SingleOrDefaultAsync(cancellationToken);

            activeCardResult = new ActiveMemberCardResult(
                activeMemberCard.Id,
                card?.Name,
                activeMemberCard.CurrentStamps,
                card?.RequiredStamps);
        }

        return new MemberScanResult(
            member.Id,
            member.Name,
            member.Email,
            activeCardResult);
    }
}
