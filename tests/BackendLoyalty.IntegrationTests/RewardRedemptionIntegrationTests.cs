using BackendLoyalty.Application.Rewards;
using BackendLoyalty.Domain.Entities;
using BackendLoyalty.Infrastructure.Persistence;
using BackendLoyalty.Infrastructure.Rewards;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Xunit;

namespace BackendLoyalty.IntegrationTests;

public sealed class RewardRedemptionIntegrationTests
{
    private const string BusinessId = "test-business-a";
    private const string OtherBusinessId = "test-business-b";
    private const string StaffId = "test-staff";
    private const string OutletId = "test-outlet";

    [Fact]
    public async Task RedeemAsync_AtomicallyMarksRewardAndWritesOneTransaction()
    {
        await using var database = await TestDatabase.CreateAsync();
        var data = await SeedAsync(database);

        await using (var db = database.OpenContext())
        {
            var result = await new RewardRedemptionService(db).RedeemAsync(
                new RedeemRewardCommand(BusinessId, StaffId, OutletId, data.Token));

            Assert.Equal("REDEEMED", result.Response.Status);
            Assert.False(string.IsNullOrWhiteSpace(result.Response.TransactionId));
        }

        await using var verify = database.OpenContext();
        var token = await verify.RewardTokens.SingleAsync();
        var reward = await verify.MemberRewards.SingleAsync();
        var transaction = await verify.Transactions.SingleAsync();

        Assert.NotNull(token.UsedAt);
        Assert.Equal(StaffId, token.UsedByStaffId);
        Assert.Equal(OutletId, token.UsedAtOutletId);
        Assert.Equal("REDEEMED", reward.Status);
        Assert.NotNull(reward.RedeemedAt);
        Assert.Equal("REWARD_REDEEMED", transaction.Type);
        Assert.Equal(BusinessId, transaction.BusinessId);
        Assert.Equal(data.MemberId, transaction.MemberId);
    }

    [Fact]
    public async Task RedeemAsync_CrossBusinessRequestIsForbiddenAndDoesNotMutate()
    {
        await using var database = await TestDatabase.CreateAsync();
        var data = await SeedAsync(database);

        await using (var db = database.OpenContext())
        {
            var ex = await Assert.ThrowsAsync<RewardRedemptionException>(() =>
                new RewardRedemptionService(db).RedeemAsync(
                    new RedeemRewardCommand(OtherBusinessId, StaffId, OutletId, data.Token)));

            Assert.Equal(RewardRedemptionErrorCode.Forbidden, ex.Code);
        }

        await using var verify = database.OpenContext();
        Assert.Null((await verify.RewardTokens.SingleAsync()).UsedAt);
        Assert.Equal("AVAILABLE", (await verify.MemberRewards.SingleAsync()).Status);
        Assert.Empty(await verify.Transactions.ToListAsync());
    }

    [Fact]
    public async Task RedeemAsync_ConcurrentCallsAllowExactlyOneTransaction()
    {
        await using var database = await TestDatabase.CreateAsync();
        var data = await SeedAsync(database);
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        async Task<RewardRedemptionErrorCode?> TryRedeemAsync()
        {
            await using var db = database.OpenContext();
            await start.Task;

            try
            {
                await new RewardRedemptionService(db).RedeemAsync(
                    new RedeemRewardCommand(BusinessId, StaffId, OutletId, data.Token));
                return null;
            }
            catch (RewardRedemptionException ex)
            {
                return ex.Code;
            }
        }

        var first = Task.Run(TryRedeemAsync);
        var second = Task.Run(TryRedeemAsync);
        start.SetResult();

        var outcomes = await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(20));
        Assert.Single(outcomes, result => result is null);
        Assert.Single(outcomes, result => result == RewardRedemptionErrorCode.Used);

        await using var verify = database.OpenContext();
        Assert.Single(await verify.Transactions.ToListAsync());
        Assert.Equal("REDEEMED", (await verify.MemberRewards.SingleAsync()).Status);
        Assert.NotNull((await verify.RewardTokens.SingleAsync()).UsedAt);
    }

    [Fact]
    public async Task RedeemAsync_UnavailableRewardDoesNotConsumeToken()
    {
        await using var database = await TestDatabase.CreateAsync();
        var data = await SeedAsync(database, rewardStatus: "REDEEMED");

        await using (var db = database.OpenContext())
        {
            var ex = await Assert.ThrowsAsync<RewardRedemptionException>(() =>
                new RewardRedemptionService(db).RedeemAsync(
                    new RedeemRewardCommand(BusinessId, StaffId, OutletId, data.Token)));

            Assert.Equal(RewardRedemptionErrorCode.Unavailable, ex.Code);
        }

        await using var verify = database.OpenContext();
        Assert.Null((await verify.RewardTokens.SingleAsync()).UsedAt);
        Assert.Empty(await verify.Transactions.ToListAsync());
    }

    private static async Task<(string MemberId, string Token)> SeedAsync(
        TestDatabase database, string rewardStatus = "AVAILABLE")
    {
        var now = DateTime.UtcNow;
        var memberId = Guid.NewGuid().ToString();
        var memberCardId = Guid.NewGuid().ToString();
        var rewardId = Guid.NewGuid().ToString();
        var token = "it_" + Guid.NewGuid().ToString("N");

        await using var db = database.OpenContext();
        db.MemberCards.Add(new MemberCard
        {
            Id = memberCardId,
            BusinessId = BusinessId,
            MemberId = memberId,
            CardId = "test-card",
            IsActive = true,
            StartedAt = now,
            CreatedAt = now,
            UpdatedAt = now,
        });
        db.MemberRewards.Add(new MemberReward
        {
            Id = rewardId,
            BusinessId = BusinessId,
            MemberId = memberId,
            MemberCardId = memberCardId,
            RewardId = "test-reward",
            Status = rewardStatus,
            IssuedAt = now,
            CreatedAt = now,
            UpdatedAt = now,
        });
        db.RewardTokens.Add(new RewardToken
        {
            Id = Guid.NewGuid().ToString(),
            BusinessId = BusinessId,
            MemberId = memberId,
            MemberCardId = memberCardId,
            MemberRewardId = rewardId,
            Token = token,
            ExpiresAt = now.AddMinutes(30),
            CreatedAt = now,
            UpdatedAt = now,
        });
        await db.SaveChangesAsync();
        return (memberId, token);
    }

    /// <summary>
    /// Each test owns a new local PostgreSQL database; only databases with this
    /// generated prefix can be dropped. No existing schema is ever reused.
    /// </summary>
    private sealed class TestDatabase : IAsyncDisposable
    {
        private readonly string _connectionString;

        private TestDatabase(string connectionString) => _connectionString = connectionString;

        public static async Task<TestDatabase> CreateAsync()
        {
            var raw = Environment.GetEnvironmentVariable("LOYALTY_TEST_POSTGRES");
            if (string.IsNullOrWhiteSpace(raw))
            {
                throw new InvalidOperationException(
                    "Set LOYALTY_TEST_POSTGRES to a disposable local PostgreSQL admin connection.");
            }

            var connection = new NpgsqlConnectionStringBuilder(raw);
            if (!string.Equals(connection.Host, "localhost", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(connection.Host, "127.0.0.1", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(connection.Host, "::1", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Integration tests require loopback PostgreSQL.");
            }

            if (!string.Equals(connection.Database, "postgres", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    "Integration tests require Database=postgres and create a separate disposable DB.");
            }

            connection.Database = "loyalty_it_" + Guid.NewGuid().ToString("N");
            connection.Pooling = false;
            var database = new TestDatabase(connection.ConnectionString);
            await using var db = database.OpenContext();
            await db.Database.EnsureCreatedAsync();
            return database;
        }

        public LoyaltyDbContext OpenContext()
        {
            var options = new DbContextOptionsBuilder<LoyaltyDbContext>()
                .UseNpgsql(_connectionString)
                .Options;
            return new LoyaltyDbContext(options);
        }

        public async ValueTask DisposeAsync()
        {
            var name = new NpgsqlConnectionStringBuilder(_connectionString).Database;
            if (name is null || !name.StartsWith("loyalty_it_", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Refusing to delete a non-test database.");
            }

            await using var db = OpenContext();
            await db.Database.EnsureDeletedAsync();
        }
    }
}
