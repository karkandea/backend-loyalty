using System.Security.Cryptography;
using System.Text;
using BackendLoyalty.Application.Members;
using BackendLoyalty.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace BackendLoyalty.Infrastructure.Members;

public sealed class MemberSessionResolver(LoyaltyDbContext dbContext) : IMemberSessionResolver
{
    public async Task<MemberSessionContext?> ResolveAsync(
        string? sessionToken,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(sessionToken))
        {
            return null;
        }

        var tokenHash = Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(sessionToken)))
            .ToLowerInvariant();
        var now = DateTime.UtcNow;

        return await (
            from session in dbContext.MemberSessions.AsNoTracking()
            join member in dbContext.Members.AsNoTracking() on session.MemberId equals member.Id
            where session.SessionTokenHash == tokenHash
                  && session.RevokedAt == null
                  && session.ExpiresAt > now
                  && member.BusinessId == session.BusinessId
            select new MemberSessionContext(
                member.Id,
                member.BusinessId,
                member.Email,
                member.Name,
                member.MemberBarcode))
            .SingleOrDefaultAsync(cancellationToken);
    }
}
