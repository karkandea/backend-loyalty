using System.Data;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BackendLoyalty.Application.Auth;
using BackendLoyalty.Domain.Entities;
using BackendLoyalty.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace BackendLoyalty.Infrastructure.Auth;

public sealed class BusinessInvitationService(
    StandaloneAuthDbContext db,
    IRefreshTokenSessionService refreshSessions) : IBusinessInvitationService
{
    private const string ManagedBySupabase = "managed-by-supabase-auth";
    private static readonly TimeSpan InvitationLifetime = TimeSpan.FromDays(7);
    private static readonly TimeSpan InvitationCooldown = TimeSpan.FromMinutes(1);

    public async Task<OwnerSignupOutcome> SignupOwnerAsync(
        OwnerSignupRequest request,
        CancellationToken cancellationToken)
    {
        var email = NormalizeEmail(request.Email);
        var slug = request.Slug.Trim().ToLowerInvariant();
        var now = DateTime.UtcNow;

        await using var transaction = await db.Database.BeginTransactionAsync(
            IsolationLevel.Serializable,
            cancellationToken);

        var existingBusiness = await db.Businesses
            .SingleOrDefaultAsync(x => x.Slug == slug, cancellationToken);

        if (existingBusiness is not null)
        {
            var activeOwner = await db.BusinessUsers.AnyAsync(
                x => x.BusinessId == existingBusiness.Id &&
                     x.IsActive &&
                     x.Email.ToLower() == email &&
                     x.Role.ToUpper() == "OWNER",
                cancellationToken);

            if (activeOwner)
            {
                await transaction.CommitAsync(cancellationToken);
                return new OwnerSignupOutcome(
                    OwnerSignupStatus.Success,
                    existingBusiness.Id,
                    existingBusiness.Slug,
                    false,
                    null);
            }

            var ownsPendingSignup = await db.BusinessInvitations.AnyAsync(
                x => x.BusinessId == existingBusiness.Id &&
                     x.Email.ToLower() == email &&
                     x.Role.ToUpper() == "OWNER" &&
                     x.UsedAt == null,
                cancellationToken);

            if (!ownsPendingSignup)
            {
                await transaction.RollbackAsync(cancellationToken);
                return new OwnerSignupOutcome(
                    OwnerSignupStatus.SlugTaken,
                    null,
                    null,
                    false,
                    null);
            }

            var identityRows = await FindActiveIdentityRowsByEmailAsync(email, cancellationToken);
            if (HasAmbiguousIdentity(identityRows))
            {
                await transaction.RollbackAsync(cancellationToken);
                return new OwnerSignupOutcome(
                    OwnerSignupStatus.DataConflict,
                    null,
                    null,
                    false,
                    null);
            }

            var issue = await IssueInvitationAsync(
                existingBusiness,
                email,
                "OWNER",
                null,
                identityRows.Count == 0,
                now,
                cancellationToken);

            await transaction.CommitAsync(cancellationToken);
            return new OwnerSignupOutcome(
                OwnerSignupStatus.Success,
                existingBusiness.Id,
                existingBusiness.Slug,
                true,
                issue);
        }

        var existingIdentityRows = await FindActiveIdentityRowsByEmailAsync(email, cancellationToken);
        if (HasAmbiguousIdentity(existingIdentityRows))
        {
            await transaction.RollbackAsync(cancellationToken);
            return new OwnerSignupOutcome(
                OwnerSignupStatus.DataConflict,
                null,
                null,
                false,
                null);
        }

        if (existingIdentityRows.Count > 0 && request.MaxBusinesses > 0)
        {
            var ownerCount = await CountOwnerMembershipsAsync(existingIdentityRows, cancellationToken);
            if (ownerCount >= request.MaxBusinesses)
            {
                await transaction.RollbackAsync(cancellationToken);
                return new OwnerSignupOutcome(
                    OwnerSignupStatus.BusinessLimitReached,
                    null,
                    null,
                    false,
                    null);
            }
        }

        var business = new Business
        {
            Id = Guid.NewGuid().ToString(),
            Name = request.BusinessName.Trim(),
            Slug = slug,
            Tier = "FREE",
            IsActive = false,
            CreatedAt = now,
            UpdatedAt = now,
        };

        db.Businesses.Add(business);
        await db.SaveChangesAsync(cancellationToken);

        var invitation = await IssueInvitationAsync(
            business,
            email,
            "OWNER",
            null,
            existingIdentityRows.Count == 0,
            now,
            cancellationToken);

        await transaction.CommitAsync(cancellationToken);
        return new OwnerSignupOutcome(
            OwnerSignupStatus.Success,
            business.Id,
            business.Slug,
            true,
            invitation);
    }

    public async Task<InvitationIssue?> ReissuePendingOwnerInvitationAsync(
        string email,
        string slug,
        CancellationToken cancellationToken)
    {
        var normalizedEmail = NormalizeEmail(email);
        var normalizedSlug = slug.Trim().ToLowerInvariant();
        var business = await db.Businesses.SingleOrDefaultAsync(
            x => x.Slug == normalizedSlug,
            cancellationToken);

        if (business is null)
            return null;

        var pendingOwnerInvite = await db.BusinessInvitations.AnyAsync(
            x => x.BusinessId == business.Id &&
                 x.Email.ToLower() == normalizedEmail &&
                 x.Role.ToUpper() == "OWNER" &&
                 x.UsedAt == null,
            cancellationToken);

        if (!pendingOwnerInvite)
            return null;

        var identityRows = await FindActiveIdentityRowsByEmailAsync(normalizedEmail, cancellationToken);
        if (HasAmbiguousIdentity(identityRows))
            return null;

        return await IssueInvitationAsync(
            business,
            normalizedEmail,
            "OWNER",
            null,
            identityRows.Count == 0,
            DateTime.UtcNow,
            cancellationToken);
    }

    public async Task<InvitationIssue?> CreateTeamInvitationAsync(
        TeamInvitationRequest request,
        CancellationToken cancellationToken)
    {
        var email = NormalizeEmail(request.Email);
        var role = request.Role.Trim().ToUpperInvariant();
        if (role is not ("ADMIN" or "STAFF"))
            return null;

        var business = await db.Businesses.SingleOrDefaultAsync(
            x => x.Id == request.BusinessId && x.IsActive,
            cancellationToken);
        if (business is null)
            return null;

        var inviterRows = await ResolveActiveIdentityRowsAsync(request.InvitedBy, cancellationToken);
        if (inviterRows.Count == 0)
            return null;

        var inviterEmail = inviterRows[0].Email.Trim().ToLowerInvariant();
        if (inviterEmail == email)
            return null;

        var existingMember = await db.BusinessUsers.AnyAsync(
            x => x.BusinessId == request.BusinessId &&
                 x.Email.ToLower() == email,
            cancellationToken);
        if (existingMember)
            return null;

        var identityRows = await FindActiveIdentityRowsByEmailAsync(email, cancellationToken);
        return await IssueInvitationAsync(
            business,
            email,
            role,
            request.InvitedBy,
            identityRows.Count == 0,
            DateTime.UtcNow,
            cancellationToken,
            request.Permissions);
    }

    public async Task<InvitationDetails?> ResolveInvitationAsync(
        string rawToken,
        CancellationToken cancellationToken)
    {
        var hash = Hash(rawToken.Trim());
        var now = DateTime.UtcNow;
        var row = await db.BusinessInvitations.AsNoTracking().SingleOrDefaultAsync(
            x => x.TokenHash == hash &&
                 x.UsedAt == null &&
                 x.RevokedAt == null &&
                 x.ExpiresAt > now,
            cancellationToken);

        if (row is null)
            return null;

        var business = await db.Businesses.AsNoTracking().SingleOrDefaultAsync(
            x => x.Id == row.BusinessId,
            cancellationToken);
        if (business is null)
            return null;

        return new InvitationDetails(
            row.Id,
            row.BusinessId,
            business.Name,
            row.Email,
            row.Role,
            ReadPermissions(row.Permissions),
            row.ExpiresAt,
            row.RequiresPassword);
    }

    public async Task<InvitationRegistrationOutcome> RegisterAsync(
        string rawToken,
        string fullName,
        string password,
        CancellationToken cancellationToken)
    {
        var tokenHash = Hash(rawToken.Trim());
        var now = DateTime.UtcNow;

        await using var transaction = await db.Database.BeginTransactionAsync(
            IsolationLevel.ReadCommitted,
            cancellationToken);

        var invite = await db.BusinessInvitations.AsNoTracking().SingleOrDefaultAsync(
            x => x.TokenHash == tokenHash &&
                 x.UsedAt == null &&
                 x.RevokedAt == null &&
                 x.ExpiresAt > now,
            cancellationToken);

        if (invite is null)
            return InvitationRegistrationOutcomeFor(InvitationRegistrationStatus.InvalidOrExpired);

        var email = NormalizeEmail(invite.Email);
        var activeIdentityRows = await FindActiveIdentityRowsByEmailAsync(email, cancellationToken);
        if (activeIdentityRows.Count > 0)
        {
            await transaction.RollbackAsync(cancellationToken);
            return InvitationRegistrationOutcomeFor(InvitationRegistrationStatus.UserExists);
        }

        var consumed = await ConsumeInvitationAsync(invite.Id, now, cancellationToken);
        if (!consumed)
        {
            await transaction.RollbackAsync(cancellationToken);
            return InvitationRegistrationOutcomeFor(InvitationRegistrationStatus.InvalidOrExpired);
        }

        var membership = await db.BusinessUsers.SingleOrDefaultAsync(
            x => x.BusinessId == invite.BusinessId && x.Email.ToLower() == email,
            cancellationToken);

        var identityId = membership?.AuthUserId;
        if (string.IsNullOrWhiteSpace(identityId))
            identityId = Guid.NewGuid().ToString();

        var passwordHash = BCrypt.Net.BCrypt.HashPassword(password);
        var role = invite.Role.Trim().ToUpperInvariant();

        if (membership is null)
        {
            membership = new BusinessUser
            {
                Id = Guid.NewGuid().ToString(),
                BusinessId = invite.BusinessId,
                AuthUserId = identityId,
                Email = email,
                PasswordHash = passwordHash,
                FullName = fullName.Trim(),
                Role = role,
                Permissions = ClonePermissions(invite.Permissions),
                IsActive = true,
                LastLoginAt = null,
                CreatedAt = now,
                UpdatedAt = now,
            };
            db.BusinessUsers.Add(membership);
        }
        else
        {
            membership.AuthUserId = identityId;
            membership.PasswordHash = passwordHash;
            membership.FullName = fullName.Trim();
            if (!string.Equals(membership.Role, "OWNER", StringComparison.OrdinalIgnoreCase))
            {
                membership.Role = role;
                membership.Permissions = ClonePermissions(invite.Permissions);
            }
            membership.IsActive = true;
            membership.UpdatedAt = now;
        }

        if (string.Equals(role, "OWNER", StringComparison.OrdinalIgnoreCase))
        {
            var business = await db.Businesses.SingleOrDefaultAsync(
                x => x.Id == invite.BusinessId,
                cancellationToken);
            if (business is null)
            {
                await transaction.RollbackAsync(cancellationToken);
                return InvitationRegistrationOutcomeFor(InvitationRegistrationStatus.Conflict);
            }

            business.IsActive = true;
            business.UpdatedAt = now;
        }

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return new InvitationRegistrationOutcome(
            InvitationRegistrationStatus.Success,
            identityId,
            invite.BusinessId,
            email);
    }

    public async Task<InvitationAcceptanceOutcome> AcceptExistingAsync(
        string userId,
        string rawToken,
        CancellationToken cancellationToken)
    {
        var tokenHash = Hash(rawToken.Trim());
        var now = DateTime.UtcNow;

        await using var transaction = await db.Database.BeginTransactionAsync(
            IsolationLevel.ReadCommitted,
            cancellationToken);

        var identityRows = await ResolveActiveIdentityRowsAsync(userId, cancellationToken);
        if (identityRows.Count == 0)
        {
            await transaction.RollbackAsync(cancellationToken);
            return InvitationAcceptanceOutcomeFor(InvitationAcceptanceStatus.IdentityNotFound);
        }

        var identityEmails = identityRows
            .Select(x => NormalizeEmail(x.Email))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (identityEmails.Count != 1)
        {
            await transaction.RollbackAsync(cancellationToken);
            return InvitationAcceptanceOutcomeFor(InvitationAcceptanceStatus.Conflict);
        }

        var invite = await db.BusinessInvitations.AsNoTracking().SingleOrDefaultAsync(
            x => x.TokenHash == tokenHash &&
                 x.UsedAt == null &&
                 x.RevokedAt == null &&
                 x.ExpiresAt > now,
            cancellationToken);
        if (invite is null)
        {
            await transaction.RollbackAsync(cancellationToken);
            return InvitationAcceptanceOutcomeFor(InvitationAcceptanceStatus.InvalidOrExpired);
        }

        if (!string.Equals(identityEmails[0], NormalizeEmail(invite.Email), StringComparison.OrdinalIgnoreCase))
        {
            await transaction.RollbackAsync(cancellationToken);
            return InvitationAcceptanceOutcomeFor(InvitationAcceptanceStatus.EmailMismatch);
        }

        var consumed = await ConsumeInvitationAsync(invite.Id, now, cancellationToken);
        if (!consumed)
        {
            await transaction.RollbackAsync(cancellationToken);
            return InvitationAcceptanceOutcomeFor(InvitationAcceptanceStatus.InvalidOrExpired);
        }

        var requiresRelogin = false;
        var identityId = identityRows.Select(x => x.AuthUserId).FirstOrDefault(x => !string.IsNullOrWhiteSpace(x));
        if (string.IsNullOrWhiteSpace(identityId))
        {
            identityId = Guid.NewGuid().ToString();
            foreach (var row in identityRows)
            {
                row.AuthUserId = identityId;
                row.UpdatedAt = now;
            }
            requiresRelogin = true;
        }

        var email = identityEmails[0];
        var membership = await db.BusinessUsers.SingleOrDefaultAsync(
            x => x.BusinessId == invite.BusinessId && x.Email.ToLower() == email,
            cancellationToken);
        var role = invite.Role.Trim().ToUpperInvariant();
        var inheritedHash = identityRows
            .Select(x => x.PasswordHash)
            .FirstOrDefault(IsUsableBcryptHash) ?? ManagedBySupabase;

        if (membership is null)
        {
            db.BusinessUsers.Add(new BusinessUser
            {
                Id = Guid.NewGuid().ToString(),
                BusinessId = invite.BusinessId,
                AuthUserId = identityId,
                Email = email,
                PasswordHash = inheritedHash,
                FullName = identityRows[0].FullName,
                Role = role,
                Permissions = ClonePermissions(invite.Permissions),
                IsActive = true,
                LastLoginAt = null,
                CreatedAt = now,
                UpdatedAt = now,
            });
        }
        else
        {
            membership.AuthUserId = identityId;
            if (!string.Equals(membership.Role, "OWNER", StringComparison.OrdinalIgnoreCase))
            {
                membership.Role = role;
                membership.Permissions = ClonePermissions(invite.Permissions);
            }
            if (!IsUsableBcryptHash(membership.PasswordHash) && IsUsableBcryptHash(inheritedHash))
                membership.PasswordHash = inheritedHash;
            membership.IsActive = true;
            membership.UpdatedAt = now;
        }

        if (string.Equals(role, "OWNER", StringComparison.OrdinalIgnoreCase))
        {
            var business = await db.Businesses.SingleOrDefaultAsync(
                x => x.Id == invite.BusinessId,
                cancellationToken);
            if (business is null)
            {
                await transaction.RollbackAsync(cancellationToken);
                return InvitationAcceptanceOutcomeFor(InvitationAcceptanceStatus.Conflict);
            }
            business.IsActive = true;
            business.UpdatedAt = now;
        }

        await db.SaveChangesAsync(cancellationToken);

        if (requiresRelogin)
        {
            await refreshSessions.RevokeAllAsync(
                userId,
                "business",
                "identity_normalized",
                cancellationToken);
        }

        var membershipCount = await db.BusinessUsers.CountAsync(
            x => x.IsActive && x.AuthUserId == identityId,
            cancellationToken);

        await transaction.CommitAsync(cancellationToken);
        return new InvitationAcceptanceOutcome(
            InvitationAcceptanceStatus.Success,
            invite.BusinessId,
            membershipCount,
            requiresRelogin);
    }

    private async Task<InvitationIssue?> IssueInvitationAsync(
        Business business,
        string email,
        string role,
        string? invitedBy,
        bool requiresPassword,
        DateTime now,
        CancellationToken cancellationToken,
        IReadOnlyList<string>? permissions = null)
    {
        var latestActive = await db.BusinessInvitations
            .Where(x => x.BusinessId == business.Id &&
                        x.Email.ToLower() == email &&
                        x.UsedAt == null &&
                        x.RevokedAt == null &&
                        x.ExpiresAt > now)
            .OrderByDescending(x => x.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken);

        if (latestActive is not null && latestActive.CreatedAt > now - InvitationCooldown)
            return null;

        await db.BusinessInvitations
            .Where(x => x.BusinessId == business.Id &&
                        x.Email.ToLower() == email &&
                        x.UsedAt == null &&
                        x.RevokedAt == null)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(x => x.RevokedAt, now)
                    .SetProperty(x => x.UpdatedAt, now),
                cancellationToken);

        var rawToken = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        var expiresAt = now + InvitationLifetime;
        var invitation = new BusinessInvitation
        {
            Id = Guid.NewGuid().ToString(),
            BusinessId = business.Id,
            Email = email,
            Role = role,
            Permissions = SerializePermissions(permissions),
            TokenHash = Hash(rawToken),
            ExpiresAt = expiresAt,
            UsedAt = null,
            RevokedAt = null,
            InvitedBy = invitedBy,
            RequiresPassword = requiresPassword,
            CreatedAt = now,
            UpdatedAt = now,
        };

        db.BusinessInvitations.Add(invitation);
        await db.SaveChangesAsync(cancellationToken);
        return new InvitationIssue(
            invitation.Id,
            business.Id,
            business.Name,
            email,
            role,
            rawToken,
            expiresAt,
            requiresPassword);
    }

    private async Task<bool> ConsumeInvitationAsync(
        string invitationId,
        DateTime now,
        CancellationToken cancellationToken)
    {
        var affected = await db.BusinessInvitations
            .Where(x => x.Id == invitationId &&
                        x.UsedAt == null &&
                        x.RevokedAt == null &&
                        x.ExpiresAt > now)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(x => x.UsedAt, now)
                    .SetProperty(x => x.UpdatedAt, now),
                cancellationToken);
        return affected == 1;
    }

    private Task<List<BusinessUser>> FindActiveIdentityRowsByEmailAsync(
        string email,
        CancellationToken cancellationToken) =>
        db.BusinessUsers
            .Where(x => x.IsActive && x.Email.ToLower() == email)
            .ToListAsync(cancellationToken);

    private Task<List<BusinessUser>> ResolveActiveIdentityRowsAsync(
        string userId,
        CancellationToken cancellationToken)
    {
        if (Guid.TryParse(userId, out var guid))
        {
            var normalized = guid.ToString();
            return db.BusinessUsers
                .Where(x => x.IsActive && (x.Id == userId || x.AuthUserId == normalized))
                .ToListAsync(cancellationToken);
        }

        return db.BusinessUsers
            .Where(x => x.IsActive && x.Id == userId)
            .ToListAsync(cancellationToken);
    }

    private async Task<int> CountOwnerMembershipsAsync(
        IReadOnlyCollection<BusinessUser> identityRows,
        CancellationToken cancellationToken)
    {
        var authUserId = identityRows.Select(x => x.AuthUserId).FirstOrDefault(x => !string.IsNullOrWhiteSpace(x));
        if (!string.IsNullOrWhiteSpace(authUserId))
        {
            return await db.BusinessUsers.CountAsync(
                x => x.IsActive && x.AuthUserId == authUserId && x.Role.ToUpper() == "OWNER",
                cancellationToken);
        }

        var ids = identityRows.Select(x => x.Id).ToArray();
        return await db.BusinessUsers.CountAsync(
            x => x.IsActive && ids.Contains(x.Id) && x.Role.ToUpper() == "OWNER",
            cancellationToken);
    }

    private static bool HasAmbiguousIdentity(IEnumerable<BusinessUser> rows) =>
        rows.GroupBy(x => string.IsNullOrWhiteSpace(x.AuthUserId) ? x.Id : x.AuthUserId!)
            .Skip(1)
            .Any();

    private static bool IsUsableBcryptHash(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.StartsWith("$2", StringComparison.Ordinal) &&
        value.Length >= 50;

    private static string NormalizeEmail(string email) => email.Trim().ToLowerInvariant();

    private static JsonDocument SerializePermissions(IReadOnlyList<string>? permissions) =>
        JsonSerializer.SerializeToDocument(permissions ?? Array.Empty<string>());

    private static JsonDocument ClonePermissions(JsonDocument? permissions) =>
        JsonDocument.Parse(permissions?.RootElement.GetRawText() ?? "[]");

    private static IReadOnlyList<string> ReadPermissions(JsonDocument? permissions)
    {
        if (permissions is null || permissions.RootElement.ValueKind != JsonValueKind.Array)
            return Array.Empty<string>();

        return permissions.RootElement
            .EnumerateArray()
            .Where(x => x.ValueKind == JsonValueKind.String)
            .Select(x => x.GetString())
            .Where(x => x is not null)
            .Select(x => x!)
            .ToArray();
    }

    private static string Hash(string token)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(token));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static InvitationRegistrationOutcome InvitationRegistrationOutcomeFor(
        InvitationRegistrationStatus status) => new(status, null, null, null);

    private static InvitationAcceptanceOutcome InvitationAcceptanceOutcomeFor(
        InvitationAcceptanceStatus status) => new(status, null, 0, false);
}
