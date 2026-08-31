using BackendLoyalty.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace BackendLoyalty.Infrastructure.Persistence;

public sealed class LoyaltyDbContext(DbContextOptions<LoyaltyDbContext> options) : DbContext(options)
{
    public DbSet<Member> Members => Set<Member>();
    public DbSet<MemberCard> MemberCards => Set<MemberCard>();
    public DbSet<Card> Cards => Set<Card>();
    public DbSet<CardMilestone> CardMilestones => Set<CardMilestone>();
    public DbSet<Reward> Rewards => Set<Reward>();
    public DbSet<MemberReward> MemberRewards => Set<MemberReward>();
    public DbSet<LoyaltyTransaction> Transactions => Set<LoyaltyTransaction>();

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
            entity.HasIndex(x => new { x.BusinessId, x.MemberId, x.IsActive }).IsUnique();
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
    }
}
