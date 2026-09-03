namespace BackendLoyalty.Application.Auth;

public sealed record BusinessPortalMembership(
    string BusinessUserId,
    string BusinessId,
    string BusinessName,
    string BusinessSlug,
    string Role);

public sealed record BusinessPortalTeamMember(
    string Id,
    string Email,
    string FullName,
    string Role,
    bool IsActive,
    DateTime? LastLoginAt);

public sealed record BusinessPortalSummary(
    string BusinessId,
    string BusinessName,
    string BusinessSlug,
    string Tier,
    string Role,
    bool HasCompletedOnboarding,
    int MemberCount,
    int ActiveCardCount,
    int RewardCount,
    int OutletCount,
    int TeamMemberCount,
    IReadOnlyList<BusinessPortalTeamMember> TeamMembers);

public interface IBusinessPortalService
{
    Task<IReadOnlyList<BusinessPortalMembership>> ListMembershipsAsync(
        string userId,
        CancellationToken cancellationToken = default);

    Task<BusinessPortalSummary?> GetSummaryAsync(
        string userId,
        string businessId,
        CancellationToken cancellationToken = default);

    Task<bool> CompleteOnboardingAsync(
        string userId,
        string businessId,
        CancellationToken cancellationToken = default);
}
