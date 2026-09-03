using BackendLoyalty.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace BackendLoyalty.Infrastructure.Persistence;

public sealed class LoyaltyDbContext(DbContextOptions<LoyaltyDbContext> options) : DbContext(options)
{
    public DbSet<Member> Members => Set<Member>();
    public DbSet<MemberSession> MemberSessions => Set<MemberSession>();
    public DbSet<MemberCard> MemberCards => Set<MemberCard>();
    public DbSet<Card> Cards => Set<Card>();
    public DbSet<CardMilestone> CardMilestones => Set<CardMilestone>();
    public DbSet<Reward> Rewards => Set<Reward>();
    public DbSet<MemberReward> MemberRewards => Set<MemberReward>();
    public DbSet<RewardToken> RewardTokens => Set<RewardToken>();
    public DbSet<LoyaltyTransaction> Transactions => Set<LoyaltyTransaction>();
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Member>(entity =>
        {
            entity.ToTable("Member");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasColumnName("id");
            entity.Property(x => x.BusinessId).HasColumnName("businessId");
            entity.Property(x => x.Name).HasColumnName("name");
            entity.Property(x => x.Email).HasColumnName("email");
            entity.Property(x => x.Phone).HasColumnName("phone");
            entity.Property(x => x.MemberBarcode).HasColumnName("memberBarcode");
            entity.Property(x => x.TotalStamps).HasColumnName("totalStamps");
            entity.Property(x => x.DateJoined).HasColumnName("dateJoined");
            entity.Property(x => x.CreatedAt).HasColumnName("createdAt");
            entity.Property(x => x.UpdatedAt).HasColumnName("updatedAt");
            entity.HasIndex(x => x.BusinessId);
            entity.HasIndex(x => new { x.BusinessId, x.MemberBarcode }).IsUnique();
        });

        modelBuilder.Entity<MemberSession>(entity =>
        {
            entity.ToTable("MemberSession");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasColumnName("id");
            entity.Property(x => x.BusinessId).HasColumnName("businessId");
            entity.Property(x => x.MemberId).HasColumnName("memberId");
            entity.Property(x => x.SessionTokenHash).HasColumnName("sessionTokenHash");
            entity.Property(x => x.ExpiresAt).HasColumnName("expiresAt");
            entity.Property(x => x.RevokedAt).HasColumnName("revokedAt");
            entity.Property(x => x.Ip).HasColumnName("ip");
            entity.Property(x => x.UserAgent).HasColumnName("userAgent");
            entity.Property(x => x.CreatedAt).HasColumnName("createdAt");
            entity.Property(x => x.UpdatedAt).HasColumnName("updatedAt");
            entity.HasIndex(x => x.SessionTokenHash).IsUnique();
            entity.HasIndex(x => new { x.BusinessId, x.MemberId });
            entity.HasIndex(x => x.ExpiresAt);
        });

        modelBuilder.Entity<MemberCard>(entity =>
        {
            entity.ToTable("MemberCard");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasColumnName("id");
            entity.Property(x => x.BusinessId).HasColumnName("businessId");
            entity.Property(x => x.MemberId).HasColumnName("memberId");
            entity.Property(x => x.CardId).HasColumnName("cardId");
            entity.Property(x => x.CurrentStamps).HasColumnName("currentStamps");
            entity.Property(x => x.IsActive).HasColumnName("isActive");
            entity.Property(x => x.StartedAt).HasColumnName("startedAt");
            entity.Property(x => x.CompletedAt).HasColumnName("completedAt");
            entity.Property(x => x.CreatedAt).HasColumnName("createdAt");
            entity.Property(x => x.UpdatedAt).HasColumnName("updatedAt");
            entity.HasIndex(x => x.BusinessId);
            entity.HasIndex(x => x.MemberId);
            entity.HasIndex(x => x.CardId);
            entity.HasIndex(x => new { x.BusinessId, x.MemberId })
                .HasDatabaseName("MemberCard_one_active_per_member_key")
                .IsUnique()
                .HasFilter("\"isActive\" = true");
        });

        modelBuilder.Entity<Card>(entity =>
        {
            entity.ToTable("Card");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasColumnName("id");
            entity.Property(x => x.BusinessId).HasColumnName("businessId");
            entity.Property(x => x.Name).HasColumnName("name");
            entity.Property(x => x.RequiredStamps).HasColumnName("requiredStamps");
            entity.Property(x => x.Status).HasColumnName("status");
            entity.Property(x => x.IsDeleted).HasColumnName("isDeleted");
            entity.Property(x => x.Level).HasColumnName("level");
            entity.Property(x => x.CreatedAt).HasColumnName("createdAt");
            entity.Property(x => x.UpdatedAt).HasColumnName("updatedAt");
            entity.HasIndex(x => x.BusinessId);
            entity.HasIndex(x => x.Status);
        });

        modelBuilder.Entity<CardMilestone>(entity =>
        {
            entity.ToTable("CardMilestone");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasColumnName("id");
            entity.Property(x => x.BusinessId).HasColumnName("businessId");
            entity.Property(x => x.CardId).HasColumnName("cardId");
            entity.Property(x => x.StampCount).HasColumnName("stampCount");
            entity.Property(x => x.SortOrder).HasColumnName("sortOrder");
            entity.Property(x => x.RewardId).HasColumnName("rewardId");
            entity.Property(x => x.RewardType).HasColumnName("rewardType");
            entity.Property(x => x.RewardValue).HasColumnName("rewardValue");
            entity.Property(x => x.Title).HasColumnName("title");
            entity.Property(x => x.Description).HasColumnName("description");
            entity.Property(x => x.CreatedAt).HasColumnName("createdAt");
            entity.Property(x => x.UpdatedAt).HasColumnName("updatedAt");
            entity.HasIndex(x => x.BusinessId);
            entity.HasIndex(x => x.CardId);
            entity.HasIndex(x => x.RewardId);
            entity.HasIndex(x => new { x.CardId, x.StampCount }).IsUnique();
        });

        modelBuilder.Entity<Reward>(entity =>
        {
            entity.ToTable("Reward");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasColumnName("id");
            entity.Property(x => x.BusinessId).HasColumnName("businessId");
            entity.Property(x => x.Name).HasColumnName("name");
            entity.Property(x => x.Description).HasColumnName("description");
            entity.Property(x => x.SourceType).HasColumnName("sourceType");
            entity.Property(x => x.DefaultExpiryDays).HasColumnName("defaultExpiryDays");
            entity.Property(x => x.CreatedAt).HasColumnName("createdAt");
            entity.Property(x => x.UpdatedAt).HasColumnName("updatedAt");
            entity.HasIndex(x => x.BusinessId);
        });

        modelBuilder.Entity<MemberReward>(entity =>
        {
            entity.ToTable("MemberReward");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasColumnName("id");
            entity.Property(x => x.BusinessId).HasColumnName("businessId");
            entity.Property(x => x.MemberId).HasColumnName("memberId");
            entity.Property(x => x.RewardId).HasColumnName("rewardId");
            entity.Property(x => x.MemberCardId).HasColumnName("memberCardId");
            entity.Property(x => x.SourceType).HasColumnName("source_type");
            entity.Property(x => x.Status).HasColumnName("status");
            entity.Property(x => x.IssuedAt).HasColumnName("issuedAt");
            entity.Property(x => x.ExpiresAt).HasColumnName("expiresAt");
            entity.Property(x => x.RedeemedAt).HasColumnName("redeemedAt");
            entity.Property(x => x.CreatedAt).HasColumnName("createdAt");
            entity.Property(x => x.UpdatedAt).HasColumnName("updatedAt");
            entity.Property(x => x.Title).HasColumnName("title");
            entity.Property(x => x.Description).HasColumnName("description");
            entity.HasIndex(x => x.BusinessId);
            entity.HasIndex(x => x.MemberId);
            entity.HasIndex(x => x.RewardId);
            entity.HasIndex(x => x.MemberCardId);
            entity.HasIndex(x => x.Status);
        });

        modelBuilder.Entity<RewardToken>(entity =>
        {
            entity.ToTable("RewardToken");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasColumnName("id");
            entity.Property(x => x.BusinessId).HasColumnName("businessId");
            entity.Property(x => x.MemberRewardId).HasColumnName("memberRewardId");
            entity.Property(x => x.MemberId).HasColumnName("memberId");
            entity.Property(x => x.MemberCardId).HasColumnName("memberCardId");
            entity.Property(x => x.Scope).HasColumnName("scope");
            entity.Property(x => x.Token).HasColumnName("token");
            entity.Property(x => x.Status).HasColumnName("status");
            entity.Property(x => x.ExpiresAt).HasColumnName("expiresAt");
            entity.Property(x => x.UsedAt).HasColumnName("usedAt");
            entity.Property(x => x.UsedByStaffId).HasColumnName("usedByStaffId");
            entity.Property(x => x.OutletId).HasColumnName("outletId");
            entity.Property(x => x.UsedAtOutletId).HasColumnName("usedAtOutletId");
            entity.Property(x => x.CreatedAt).HasColumnName("createdAt");
            entity.Property(x => x.UpdatedAt).HasColumnName("updatedAt");
            entity.HasIndex(x => x.BusinessId);
            entity.HasIndex(x => x.MemberRewardId);
            entity.HasIndex(x => x.MemberId);
            entity.HasIndex(x => x.MemberCardId);
            entity.HasIndex(x => x.Scope);
            entity.HasIndex(x => x.UsedByStaffId);
            entity.HasIndex(x => x.OutletId);
            entity.HasIndex(x => x.UsedAtOutletId);
            entity.HasIndex(x => x.Token).IsUnique();
        });

        modelBuilder.Entity<LoyaltyTransaction>(entity =>
        {
            entity.ToTable("Transaction");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasColumnName("id");
            entity.Property(x => x.BusinessId).HasColumnName("businessId");
            entity.Property(x => x.MemberId).HasColumnName("memberId");
            entity.Property(x => x.MemberCardId).HasColumnName("memberCardId");
            entity.Property(x => x.CardId).HasColumnName("cardId");
            entity.Property(x => x.RewardId).HasColumnName("rewardId");
            entity.Property(x => x.OutletId).HasColumnName("outletId");
            entity.Property(x => x.StaffId).HasColumnName("staffId");
            entity.Property(x => x.Type).HasColumnName("type");
            entity.Property(x => x.StampsAdded).HasColumnName("stamps_added");
            entity.Property(x => x.CreatedAt).HasColumnName("createdAt");
            entity.Property(x => x.UpdatedAt).HasColumnName("updatedAt");
            entity.HasIndex(x => x.BusinessId);
            entity.HasIndex(x => x.MemberId);
            entity.HasIndex(x => x.MemberCardId);
            entity.HasIndex(x => x.CardId);
            entity.HasIndex(x => x.OutletId);
            entity.HasIndex(x => x.StaffId);
            entity.HasIndex(x => x.Type);
            entity.HasIndex(x => x.CreatedAt);
        });

        modelBuilder.Entity<AuditLog>(entity =>
        {
            entity.ToTable("AuditLog");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasColumnName("id").HasColumnType("uuid");
            entity.Property(x => x.BusinessId).HasColumnName("businessId").HasColumnType("uuid");
            entity.Property(x => x.UserId).HasColumnName("userId").HasColumnType("uuid");
            entity.Property(x => x.Role).HasColumnName("role");
            entity.Property(x => x.OutletId).HasColumnName("outletId").HasColumnType("uuid");
            entity.Property(x => x.ActionType).HasColumnName("actionType");
            entity.Property(x => x.ResourceType).HasColumnName("resourceType");
            entity.Property(x => x.ResourceId).HasColumnName("resourceId");
            entity.Property(x => x.Summary).HasColumnName("summary");
            entity.Property(x => x.MetadataJson).HasColumnName("metadata").HasColumnType("jsonb");
            entity.Property(x => x.Ip).HasColumnName("ip");
            entity.Property(x => x.UserAgent).HasColumnName("userAgent");
            entity.Property(x => x.CreatedAt).HasColumnName("createdAt");
            entity.HasIndex(x => new { x.BusinessId, x.CreatedAt });
            entity.HasIndex(x => x.ActionType);
        });
    }
}
