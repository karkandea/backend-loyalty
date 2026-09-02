using BackendLoyalty.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace BackendLoyalty.Infrastructure.Persistence;

public sealed class StandaloneAuthDbContext(DbContextOptions<StandaloneAuthDbContext> options) : DbContext(options)
{
    public DbSet<Business> Businesses => Set<Business>();
    public DbSet<BusinessUser> BusinessUsers => Set<BusinessUser>();
    public DbSet<BusinessInvitation> BusinessInvitations => Set<BusinessInvitation>();
    public DbSet<AdminUser> AdminUsers => Set<AdminUser>();
    public DbSet<Outlet> Outlets => Set<Outlet>();
    public DbSet<AuthRefreshSession> AuthRefreshSessions => Set<AuthRefreshSession>();
    public DbSet<AuthPasswordReset> AuthPasswordResets => Set<AuthPasswordReset>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        var authUserIdConverter = new ValueConverter<string?, Guid?>(
            value => string.IsNullOrWhiteSpace(value) ? null : Guid.Parse(value),
            value => value.HasValue ? value.Value.ToString() : null);
        var uuidStringConverter = new ValueConverter<string, Guid>(
            value => Guid.Parse(value),
            value => value.ToString());
        var optionalUuidStringConverter = new ValueConverter<string?, Guid?>(
            value => string.IsNullOrWhiteSpace(value)
                ? null
                : Guid.TryParse(value, out var parsed) ? parsed : null,
            value => value.HasValue ? value.Value.ToString() : null);

        modelBuilder.Entity<Business>(entity =>
        {
            entity.ToTable("Business");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasColumnName("id");
            entity.Property(x => x.Name).HasColumnName("name");
            entity.Property(x => x.Slug).HasColumnName("slug");
            entity.Property(x => x.Tier).HasColumnName("tier");
            entity.Property(x => x.IsActive).HasColumnName("isActive");
            entity.Property(x => x.CreatedAt).HasColumnName("createdAt");
            entity.Property(x => x.UpdatedAt).HasColumnName("updatedAt");
            entity.HasIndex(x => x.Slug).IsUnique();
        });

        modelBuilder.Entity<BusinessUser>(entity =>
        {
            entity.ToTable("BusinessUser");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasColumnName("id");
            entity.Property(x => x.BusinessId).HasColumnName("businessId");
            entity.Property(x => x.AuthUserId)
                .HasColumnName("authUserId")
                .HasColumnType("uuid")
                .HasConversion(authUserIdConverter);
            entity.Property(x => x.Email).HasColumnName("email");
            entity.Property(x => x.PasswordHash).HasColumnName("passwordHash");
            entity.Property(x => x.FullName).HasColumnName("fullName");
            entity.Property(x => x.Role).HasColumnName("role");
            entity.Property(x => x.IsActive).HasColumnName("isActive");
            entity.Property(x => x.LastLoginAt).HasColumnName("lastLoginAt");
            entity.Property(x => x.CreatedAt).HasColumnName("createdAt");
            entity.Property(x => x.UpdatedAt).HasColumnName("updatedAt");
            entity.HasIndex(x => new { x.BusinessId, x.Email }).IsUnique();
            entity.HasIndex(x => x.AuthUserId);
        });

        modelBuilder.Entity<BusinessInvitation>(entity =>
        {
            entity.ToTable("BusinessInvitation");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id)
                .HasColumnName("id")
                .HasColumnType("uuid")
                .HasConversion(uuidStringConverter);
            entity.Property(x => x.BusinessId).HasColumnName("businessId");
            entity.Property(x => x.Email).HasColumnName("email");
            entity.Property(x => x.Role).HasColumnName("role");
            entity.Property(x => x.TokenHash).HasColumnName("tokenHash");
            entity.Property(x => x.ExpiresAt).HasColumnName("expiresAt");
            entity.Property(x => x.UsedAt).HasColumnName("usedAt");
            entity.Property(x => x.RevokedAt).HasColumnName("revokedAt");
            entity.Property(x => x.InvitedBy)
                .HasColumnName("invitedBy")
                .HasColumnType("uuid")
                .HasConversion(optionalUuidStringConverter);
            entity.Property(x => x.RequiresPassword).HasColumnName("requiresPassword");
            entity.Property(x => x.CreatedAt).HasColumnName("createdAt");
            entity.Property(x => x.UpdatedAt).HasColumnName("updatedAt");
            entity.HasIndex(x => x.TokenHash).IsUnique();
            entity.HasIndex(x => x.BusinessId);
            entity.HasIndex(x => x.Email);
        });

        modelBuilder.Entity<AdminUser>(entity =>
        {
            entity.ToTable("AdminUser");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasColumnName("id");
            entity.Property(x => x.BusinessId).HasColumnName("businessId");
            entity.Property(x => x.OutletId).HasColumnName("outletId");
            entity.Property(x => x.Email).HasColumnName("email");
            entity.Property(x => x.PasswordHash).HasColumnName("passwordHash");
            entity.Property(x => x.FullName).HasColumnName("fullName");
            entity.Property(x => x.Role).HasColumnName("role");
            entity.Property(x => x.IsActive).HasColumnName("isActive");
            entity.Property(x => x.LastLoginAt).HasColumnName("lastLoginAt");
            entity.Property(x => x.CreatedAt).HasColumnName("createdAt");
            entity.Property(x => x.UpdatedAt).HasColumnName("updatedAt");
            entity.HasIndex(x => new { x.BusinessId, x.Email }).IsUnique();
            entity.HasIndex(x => x.OutletId);
        });

        modelBuilder.Entity<Outlet>(entity =>
        {
            entity.ToTable("Outlet");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.BusinessId).HasColumnName("businessId");
            entity.Property(x => x.Name).HasColumnName("name");
            entity.Property(x => x.IsActive).HasColumnName("isActive");
        });

        modelBuilder.Entity<AuthRefreshSession>(entity =>
        {
            entity.ToTable("AuthRefreshSession");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasColumnName("id");
            entity.Property(x => x.UserId).HasColumnName("userId");
            entity.Property(x => x.AuthKind).HasColumnName("authKind");
            entity.Property(x => x.TokenHash).HasColumnName("tokenHash");
            entity.Property(x => x.FamilyId).HasColumnName("familyId");
            entity.Property(x => x.ParentSessionId).HasColumnName("parentSessionId");
            entity.Property(x => x.Role).HasColumnName("role");
            entity.Property(x => x.BusinessId).HasColumnName("businessId");
            entity.Property(x => x.OutletId).HasColumnName("outletId");
            entity.Property(x => x.ExpiresAt).HasColumnName("expiresAt");
            entity.Property(x => x.RevokedAt).HasColumnName("revokedAt");
            entity.Property(x => x.RevokeReason).HasColumnName("revokeReason");
            entity.Property(x => x.ReplacedBySessionId).HasColumnName("replacedBySessionId");
            entity.Property(x => x.CreatedAt).HasColumnName("createdAt");
            entity.HasIndex(x => x.TokenHash).IsUnique();
            entity.HasIndex(x => new { x.UserId, x.AuthKind });
            entity.HasIndex(x => x.FamilyId);
            entity.HasIndex(x => x.ExpiresAt);
        });

        modelBuilder.Entity<AuthPasswordReset>(entity =>
        {
            entity.ToTable("AuthPasswordReset");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasColumnName("id");
            entity.Property(x => x.UserId).HasColumnName("userId");
            entity.Property(x => x.AuthKind).HasColumnName("authKind");
            entity.Property(x => x.Email).HasColumnName("email");
            entity.Property(x => x.TokenHash).HasColumnName("tokenHash");
            entity.Property(x => x.ExpiresAt).HasColumnName("expiresAt");
            entity.Property(x => x.UsedAt).HasColumnName("usedAt");
            entity.Property(x => x.Ip).HasColumnName("ip");
            entity.Property(x => x.UserAgent).HasColumnName("userAgent");
            entity.Property(x => x.CreatedAt).HasColumnName("createdAt");
            entity.HasIndex(x => x.TokenHash).IsUnique();
            entity.HasIndex(x => new { x.UserId, x.AuthKind });
            entity.HasIndex(x => x.ExpiresAt);
        });
    }
}
