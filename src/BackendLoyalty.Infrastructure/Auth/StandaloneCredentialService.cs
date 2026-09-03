using System.Data.Common;
using BackendLoyalty.Application.Auth;
using BackendLoyalty.Domain.Entities;
using BackendLoyalty.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace BackendLoyalty.Infrastructure.Auth;

public sealed class StandaloneCredentialService(StandaloneAuthDbContext db) : IStandaloneCredentialService
{
    private const string ManagedBySupabase = "managed-by-supabase-auth";

    public async Task<AdminLoginProfile> AuthenticateAdminAsync(
        string email,
        string password,
        CancellationToken cancellationToken)
    {
        var normalizedEmail = NormalizeEmail(email);
        var candidates = await db.AdminUsers
            .Where(x => x.Email.ToLower() == normalizedEmail)
            .ToListAsync(cancellationToken);

        if (candidates.Count == 0)
            throw InvalidCredentials();

        var activeCandidates = candidates.Where(x => x.IsActive).ToList();
        if (activeCandidates.Count == 0)
            throw new CredentialException(CredentialFailureReason.InactiveAccount, "Account is inactive or not found");

        AdminUser? matched = null;
        foreach (var candidate in activeCandidates)
        {
            if (await VerifyAndAdoptHashAsync(candidate.Id, candidate.PasswordHash, password, cancellationToken))
            {
                matched = candidate;
                break;
            }
        }

        if (matched is null)
        {
            if (activeCandidates.All(x => !IsUsableBcryptHash(x.PasswordHash)) &&
                !await HasLegacyAuthPasswordAsync(activeCandidates[0].Id, cancellationToken))
            {
                throw PasswordMigrationRequired();
            }

            throw InvalidCredentials();
        }

        if (!IsUsableBcryptHash(matched.PasswordHash))
        {
            var legacyHash = await GetLegacyAuthPasswordHashAsync(matched.Id, cancellationToken);
            if (string.IsNullOrWhiteSpace(legacyHash) || !IsUsableBcryptHash(legacyHash))
                throw PasswordMigrationRequired();

            matched.PasswordHash = legacyHash;
        }

        var business = await db.Businesses.SingleOrDefaultAsync(x => x.Id == matched.BusinessId, cancellationToken);
        if (business is null || !business.IsActive)
            throw new CredentialException(CredentialFailureReason.InactiveBusiness, "Business is inactive");

        if (string.IsNullOrWhiteSpace(matched.OutletId))
            throw new CredentialException(CredentialFailureReason.OutletRequired, "Outlet not assigned to this user");

        var outlet = await db.Outlets.SingleOrDefaultAsync(x => x.Id == matched.OutletId, cancellationToken);
        if (outlet is null || !outlet.IsActive || outlet.BusinessId != matched.BusinessId)
            throw new CredentialException(CredentialFailureReason.InactiveOutlet, "Outlet is inactive or not found");

        var role = matched.Role.Trim().ToLowerInvariant();
        if (role is not ("staff" or "manager"))
            throw new CredentialException(CredentialFailureReason.InactiveAccount, "Only staff or manager accounts can use this endpoint");

        matched.LastLoginAt = DateTime.UtcNow;
        matched.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(cancellationToken);

        return new AdminLoginProfile(
            matched.Id,
            matched.Id,
            business.Id,
            business.Name,
            outlet.Id,
            outlet.Name,
            role);
    }

    public async Task<BusinessLoginProfile> AuthenticateBusinessAsync(
        string email,
        string password,
        CancellationToken cancellationToken)
    {
        var normalizedEmail = NormalizeEmail(email);
        var rows = await db.BusinessUsers
            .Where(x => x.Email.ToLower() == normalizedEmail && x.IsActive)
            .ToListAsync(cancellationToken);

        if (rows.Count == 0)
            return await AuthenticateZeroMembershipIdentityAsync(normalizedEmail, password, cancellationToken);

        var groupedByAuthUser = rows
            .GroupBy(x => string.IsNullOrWhiteSpace(x.AuthUserId) ? x.Id : x.AuthUserId!)
            .ToList();

        List<BusinessUser>? matchedGroup = null;
        string? userId = null;
        string? adoptedHash = null;

        foreach (var group in groupedByAuthUser)
        {
            var groupRows = group.ToList();
            var localHash = groupRows.Select(x => x.PasswordHash).FirstOrDefault(IsUsableBcryptHash);
            if (localHash is not null && BCrypt.Net.BCrypt.Verify(password, localHash))
            {
                matchedGroup = groupRows;
                userId = group.Key;
                adoptedHash = localHash;
                break;
            }

            var legacyHash = await GetLegacyAuthPasswordHashAsync(group.Key, cancellationToken);
            if (IsUsableBcryptHash(legacyHash) && BCrypt.Net.BCrypt.Verify(password, legacyHash))
            {
                matchedGroup = groupRows;
                userId = group.Key;
                adoptedHash = legacyHash;
                break;
            }
        }

        if (matchedGroup is null || userId is null || adoptedHash is null)
        {
            var hasAnyLocalHash = rows.Any(x => IsUsableBcryptHash(x.PasswordHash));
            var hasAnyLegacyHash = false;
            foreach (var group in groupedByAuthUser)
            {
                if (await HasLegacyAuthPasswordAsync(group.Key, cancellationToken))
                {
                    hasAnyLegacyHash = true;
                    break;
                }
            }

            if (!hasAnyLocalHash && !hasAnyLegacyHash)
                throw PasswordMigrationRequired();

            throw InvalidCredentials();
        }

        var now = DateTime.UtcNow;
        foreach (var row in matchedGroup)
        {
            if (!IsUsableBcryptHash(row.PasswordHash))
                row.PasswordHash = adoptedHash;
            row.LastLoginAt = now;
            row.UpdatedAt = now;
        }

        var businessIds = matchedGroup.Select(x => x.BusinessId).Distinct().ToArray();
        var businesses = await db.Businesses
            .Where(x => businessIds.Contains(x.Id) && x.IsActive)
            .ToDictionaryAsync(x => x.Id, cancellationToken);

        var memberships = matchedGroup
            .Where(x => businesses.ContainsKey(x.BusinessId))
            .Select(x =>
            {
                var business = businesses[x.BusinessId];
                return new BusinessMembership(
                    x.Id,
                    x.BusinessId,
                    business.Name,
                    business.Slug,
                    x.Role.Trim().ToLowerInvariant());
            })
            .Where(x => x.Role is "owner" or "admin" or "staff")
            .ToList();

        if (memberships.Count == 0)
            throw new CredentialException(CredentialFailureReason.InactiveBusiness, "Business is inactive");

        await db.SaveChangesAsync(cancellationToken);
        return new BusinessLoginProfile(userId, memberships);
    }

    public async Task<BusinessMembership?> ResolveBusinessMembershipAsync(
        string userId,
        string businessId,
        CancellationToken cancellationToken)
    {
        BusinessUser? membership;
        if (Guid.TryParse(userId, out var authUserGuid))
        {
            var normalizedAuthUserId = authUserGuid.ToString();
            membership = await db.BusinessUsers
                .Where(x => x.BusinessId == businessId && x.IsActive &&
                            (x.Id == userId || x.AuthUserId == normalizedAuthUserId))
                .SingleOrDefaultAsync(cancellationToken);
        }
        else
        {
            membership = await db.BusinessUsers
                .Where(x => x.BusinessId == businessId && x.IsActive && x.Id == userId)
                .SingleOrDefaultAsync(cancellationToken);
        }

        if (membership is null)
            return null;

        var business = await db.Businesses
            .SingleOrDefaultAsync(x => x.Id == businessId && x.IsActive, cancellationToken);

        if (business is null)
            return null;

        var role = membership.Role.Trim().ToLowerInvariant();
        if (role is not ("owner" or "admin" or "staff"))
            return null;

        return new BusinessMembership(
            membership.Id,
            business.Id,
            business.Name,
            business.Slug,
            role);
    }

    public async Task<int> CountBusinessMembershipsAsync(
        string userId,
        CancellationToken cancellationToken)
    {
        if (Guid.TryParse(userId, out var authUserGuid))
        {
            var normalizedAuthUserId = authUserGuid.ToString();
            return await db.BusinessUsers.CountAsync(
                x => x.IsActive && (x.Id == userId || x.AuthUserId == normalizedAuthUserId),
                cancellationToken);
        }

        return await db.BusinessUsers.CountAsync(
            x => x.IsActive && x.Id == userId,
            cancellationToken);
    }

    public async Task<bool> IsUserActiveAsync(
        string userId,
        string authKind,
        CancellationToken cancellationToken)
    {
        if (string.Equals(authKind, "admin", StringComparison.OrdinalIgnoreCase))
            return await db.AdminUsers.AnyAsync(x => x.Id == userId && x.IsActive, cancellationToken);

        bool hasMembership;
        if (Guid.TryParse(userId, out var authUserGuid))
        {
            var normalizedAuthUserId = authUserGuid.ToString();
            hasMembership = await db.BusinessUsers.AnyAsync(
                x => x.IsActive && (x.Id == userId || x.AuthUserId == normalizedAuthUserId),
                cancellationToken);
        }
        else
        {
            hasMembership = await db.BusinessUsers.AnyAsync(
                x => x.IsActive && x.Id == userId,
                cancellationToken);
        }

        if (hasMembership)
            return true;

        return await IsStandaloneIdentityActiveAsync(userId, cancellationToken);
    }

    private async Task<BusinessLoginProfile> AuthenticateZeroMembershipIdentityAsync(
        string normalizedEmail,
        string password,
        CancellationToken cancellationToken)
    {
        var identity = await GetStandaloneIdentityByEmailAsync(normalizedEmail, cancellationToken);
        if (identity is null)
            throw InvalidCredentials();

        var now = DateTime.UtcNow;
        if (identity.DeletedAt is not null ||
            (identity.BannedUntil is not null && identity.BannedUntil > now))
        {
            throw new CredentialException(CredentialFailureReason.InactiveAccount, "Account has been disabled");
        }

        if (identity.EmailConfirmedAt is null)
        {
            throw new CredentialException(CredentialFailureReason.InactiveAccount, "Email address is not confirmed yet");
        }

        if (!identity.HasPassword)
            throw InvalidCredentials();

        var legacyHash = await GetLegacyAuthPasswordHashAsync(identity.UserId, cancellationToken);
        if (!IsUsableBcryptHash(legacyHash))
            throw PasswordMigrationRequired();

        if (!BCrypt.Net.BCrypt.Verify(password, legacyHash))
            throw InvalidCredentials();

        var role = string.IsNullOrWhiteSpace(identity.ResolvedRole)
            ? null
            : identity.ResolvedRole.Trim().ToLowerInvariant();

        return new BusinessLoginProfile(
            identity.UserId,
            Array.Empty<BusinessMembership>(),
            role);
    }

    private async Task<StandaloneIdentitySnapshot?> GetStandaloneIdentityByEmailAsync(
        string normalizedEmail,
        CancellationToken cancellationToken)
    {
        var connection = db.Database.GetDbConnection();
        var shouldClose = connection.State != System.Data.ConnectionState.Open;

        try
        {
            if (shouldClose)
                await connection.OpenAsync(cancellationToken);

            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT id::text, "resolvedRole", "emailConfirmedAt", "deletedAt", "bannedUntil", "hasPassword"
                FROM "StandaloneAuthIdentity"
                WHERE lower(trim(email)) = @email
                LIMIT 2
                """;
            AddParameter(command, "email", normalizedEmail);

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
                return null;

            var snapshot = new StandaloneIdentitySnapshot(
                reader.GetString(0),
                reader.IsDBNull(1) ? null : reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetFieldValue<DateTime>(2),
                reader.IsDBNull(3) ? null : reader.GetFieldValue<DateTime>(3),
                reader.IsDBNull(4) ? null : reader.GetFieldValue<DateTime>(4),
                reader.GetBoolean(5));

            if (await reader.ReadAsync(cancellationToken))
                throw new CredentialException(CredentialFailureReason.InactiveAccount, "Ambiguous account identity");

            return snapshot;
        }
        catch (PostgresException ex) when (ex.SqlState is PostgresErrorCodes.UndefinedTable or PostgresErrorCodes.InvalidSchemaName)
        {
            return null;
        }
        finally
        {
            if (shouldClose && connection.State == System.Data.ConnectionState.Open)
                await connection.CloseAsync();
        }
    }

    private async Task<bool> IsStandaloneIdentityActiveAsync(
        string userId,
        CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(userId, out var parsedUserId))
            return false;

        var connection = db.Database.GetDbConnection();
        var shouldClose = connection.State != System.Data.ConnectionState.Open;

        try
        {
            if (shouldClose)
                await connection.OpenAsync(cancellationToken);

            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT EXISTS (
                    SELECT 1
                    FROM "StandaloneAuthIdentity"
                    WHERE id = @userId
                      AND "emailConfirmedAt" IS NOT NULL
                      AND "deletedAt" IS NULL
                      AND ("bannedUntil" IS NULL OR "bannedUntil" <= now())
                      AND "hasPassword" = true
                )
                """;
            AddParameter(command, "userId", parsedUserId);

            var value = await command.ExecuteScalarAsync(cancellationToken);
            return value is bool active && active;
        }
        catch (PostgresException ex) when (ex.SqlState is PostgresErrorCodes.UndefinedTable or PostgresErrorCodes.InvalidSchemaName)
        {
            return false;
        }
        finally
        {
            if (shouldClose && connection.State == System.Data.ConnectionState.Open)
                await connection.CloseAsync();
        }
    }

    private async Task<bool> VerifyAndAdoptHashAsync(
        string legacyAuthUserId,
        string storedHash,
        string password,
        CancellationToken cancellationToken)
    {
        if (IsUsableBcryptHash(storedHash))
            return BCrypt.Net.BCrypt.Verify(password, storedHash);

        var legacyHash = await GetLegacyAuthPasswordHashAsync(legacyAuthUserId, cancellationToken);
        return IsUsableBcryptHash(legacyHash) && BCrypt.Net.BCrypt.Verify(password, legacyHash);
    }

    private async Task<bool> HasLegacyAuthPasswordAsync(string userId, CancellationToken cancellationToken) =>
        IsUsableBcryptHash(await GetLegacyAuthPasswordHashAsync(userId, cancellationToken));

    private async Task<string?> GetLegacyAuthPasswordHashAsync(
        string userId,
        CancellationToken cancellationToken)
    {
        var connection = db.Database.GetDbConnection();
        var shouldClose = connection.State != System.Data.ConnectionState.Open;

        try
        {
            if (shouldClose)
                await connection.OpenAsync(cancellationToken);

            return await TryGetPasswordHashAsync(
                connection,
                "SELECT \"passwordHash\" FROM \"LegacyAuthUserPassword\" WHERE \"authUserId\" = @userId LIMIT 1",
                userId,
                cancellationToken);
        }
        finally
        {
            if (shouldClose && connection.State == System.Data.ConnectionState.Open)
                await connection.CloseAsync();
        }
    }

    private static async Task<string?> TryGetPasswordHashAsync(
        DbConnection connection,
        string commandText,
        string userId,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = commandText;
            AddParameter(command, "userId", userId);

            var value = await command.ExecuteScalarAsync(cancellationToken);
            return value is null or DBNull ? null : Convert.ToString(value);
        }
        catch (PostgresException ex) when (ex.SqlState is PostgresErrorCodes.UndefinedTable or PostgresErrorCodes.InvalidSchemaName)
        {
            return null;
        }
    }

    private static void AddParameter(DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }

    private static bool IsUsableBcryptHash(string? hash) =>
        !string.IsNullOrWhiteSpace(hash) &&
        !string.Equals(hash, ManagedBySupabase, StringComparison.OrdinalIgnoreCase) &&
        (hash.StartsWith("$2a$", StringComparison.Ordinal) ||
         hash.StartsWith("$2b$", StringComparison.Ordinal) ||
         hash.StartsWith("$2y$", StringComparison.Ordinal));

    private static string NormalizeEmail(string email) => email.Trim().ToLowerInvariant();

    private static CredentialException InvalidCredentials() =>
        new(CredentialFailureReason.InvalidCredentials, "Invalid email or password");

    private static CredentialException PasswordMigrationRequired() =>
        new(
            CredentialFailureReason.PasswordMigrationRequired,
            "Password migration is required for this account. Import the legacy password bridge or reset the password.");

    private sealed record StandaloneIdentitySnapshot(
        string UserId,
        string? ResolvedRole,
        DateTime? EmailConfirmedAt,
        DateTime? DeletedAt,
        DateTime? BannedUntil,
        bool HasPassword);
}
