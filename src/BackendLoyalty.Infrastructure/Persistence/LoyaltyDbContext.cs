using BackendLoyalty.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace BackendLoyalty.Infrastructure.Persistence;

public sealed class LoyaltyDbContext(DbContextOptions<LoyaltyDbContext> options) : DbContext(options)
{
    public DbSet<Member> Members => Set<Member>();
    public DbSet<MemberCard> MemberCards => Set<MemberCard>();
    public DbSet<Card> Cards => Set<Card>();

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
            entity.HasIndex(x => x.MemberId);
            entity.HasIndex(x => x.CardId);
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
        });
    }
}
