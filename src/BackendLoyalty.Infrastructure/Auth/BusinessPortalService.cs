using System.Data;
using System.Data.Common;
using BackendLoyalty.Application.Auth;
using BackendLoyalty.Domain.Entities;
using BackendLoyalty.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace BackendLoyalty.Infrastructure.Auth;

public sealed class BusinessPortalService(
    StandaloneAuthDbContext authDb,
    LoyaltyDbContext loyaltyDb) : IBusinessPortalService
{
    public async Task<IReadOnlyList<BusinessPortalMembership>> ListMembershipsAsync(
        string userId,
        CancellationToken cancellationToken = default)
    {
        var memberships = await MembershipsForUser(userId)
            .AsNoTracking()
            .Where(x => x.IsActive)
            .ToListAsync(cancellationToken);

        if (memberships.Count == 0)
            return Array.Empty<BusinessPortalMembership>();

        var businessIds = memberships.Select(x => x.BusinessId).Distinct().ToArray();
        var businesses = await authDb.Businesses
            .AsNoTracking()
            .Where(x => businessIds.Contains(x.Id) && x.IsActive)
            .ToDictionaryAsync(x => x.Id, cancellationToken);

        return memberships
            .Where(x => businesses.ContainsKey(x.BusinessId))
            .Select(x =>
            {
                var business = businesses[x.BusinessId];
                return new BusinessPortalMembership(
                    x.Id,
                    business.Id,
                    business.Name,
                    business.Slug,
                    x.Role.Trim().ToLowerInvariant());
            })
            .Where(x => x.Role is "owner" or "admin" or "staff")
            .OrderBy(x => x.BusinessName)
            .ToArray();
    }

    public async Task<BusinessPortalSummary?> GetSummaryAsync(
        string userId,
        string businessId,
        CancellationToken cancellationToken = default)
    {
        var membership = await MembershipsForUser(userId)
            .AsNoTracking()
            .SingleOrDefaultAsync(
                x => x.BusinessId == businessId && x.IsActive,
                cancellationToken);
        if (membership is null)
            return null;

        var role = membership.Role.Trim().ToLowerInvariant();
        if (role is not ("owner" or "admin" or "staff"))
            return null;

        var business = await authDb.Businesses
            .AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == businessId && x.IsActive, cancellationToken);
        if (business is null)
            return null;

        var hasCompletedOnboarding = await ReadHasCompletedOnboardingAsync(businessId, cancellationToken);

        var memberCount = await loyaltyDb.Members
            .AsNoTracking()
            .CountAsync(x => x.BusinessId == businessId, cancellationToken);
        var activeCardCount = await loyaltyDb.Cards
            .AsNoTracking()
            .CountAsync(x => x.BusinessId == businessId && x.Status == "ACTIVE" && !x.IsDeleted, cancellationToken);
        var rewardCount = await loyaltyDb.Rewards
            .AsNoTracking()
            .CountAsync(x => x.BusinessId == businessId, cancellationToken);
        var outletCount = await authDb.Outlets
            .AsNoTracking()
            .CountAsync(x => x.BusinessId == businessId && x.IsActive, cancellationToken);

        var teamMembers = await authDb.BusinessUsers
            .AsNoTracking()
            .Where(x => x.BusinessId == businessId)
            .OrderBy(x => x.FullName)
            .Select(x => new BusinessPortalTeamMember(
                x.Id,
                x.Email,
                x.FullName,
                x.Role,
                x.IsActive,
                x.LastLoginAt))
            .ToListAsync(cancellationToken);

        return new BusinessPortalSummary(
            business.Id,
            business.Name,
            business.Slug,
            business.Tier,
            role,
            hasCompletedOnboarding,
            memberCount,
            activeCardCount,
            rewardCount,
            outletCount,
            teamMembers.Count(x => x.IsActive),
            teamMembers);
    }

    public async Task<bool> CompleteOnboardingAsync(
        string userId,
        string businessId,
        CancellationToken cancellationToken = default)
    {
        var membership = await MembershipsForUser(userId)
            .AsNoTracking()
            .SingleOrDefaultAsync(
                x => x.BusinessId == businessId && x.IsActive,
                cancellationToken);
        if (membership is null)
            return false;

        var role = membership.Role.Trim().ToLowerInvariant();
        if (role is not ("owner" or "admin"))
            return false;

        var connection = authDb.Database.GetDbConnection();
        var shouldClose = connection.State != ConnectionState.Open;
        try
        {
            if (shouldClose)
                await connection.OpenAsync(cancellationToken);

            await using var command = connection.CreateCommand();
            command.CommandText = """
                UPDATE "Business"
                SET "hasCompletedOnboarding" = true,
                    "updatedAt" = now()
                WHERE id = @businessId
                  AND "isActive" = true
                """;
            AddParameter(command, "businessId", businessId);
            return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
        }
        finally
        {
            if (shouldClose && connection.State == ConnectionState.Open)
                await connection.CloseAsync();
        }
    }

    private IQueryable<BusinessUser> MembershipsForUser(string userId)
    {
        if (Guid.TryParse(userId, out var parsed))
        {
            var normalized = parsed.ToString();
            return authDb.BusinessUsers.Where(x => x.Id == userId || x.AuthUserId == normalized);
        }

        return authDb.BusinessUsers.Where(x => x.Id == userId);
    }

    private async Task<bool> ReadHasCompletedOnboardingAsync(
        string businessId,
        CancellationToken cancellationToken)
    {
        var connection = authDb.Database.GetDbConnection();
        var shouldClose = connection.State != ConnectionState.Open;
        try
        {
            if (shouldClose)
                await connection.OpenAsync(cancellationToken);

            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT \"hasCompletedOnboarding\" FROM \"Business\" WHERE id = @businessId LIMIT 1";
            AddParameter(command, "businessId", businessId);
            var value = await command.ExecuteScalarAsync(cancellationToken);
            return value is bool completed && completed;
        }
        finally
        {
            if (shouldClose && connection.State == ConnectionState.Open)
                await connection.CloseAsync();
        }
    }

    private static void AddParameter(DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }
}
