using BackendLoyalty.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace BackendLoyalty.Infrastructure.Persistence;

public sealed class StandaloneAuthDbContext(DbContextOptions<StandaloneAuthDbContext> options) : DbContext(options)
{
    public DbSet<Business> Businesses => Set<Business>();
    public DbSet<BusinessUser> BusinessUsers => Set<BusinessUser>();
    public DbSet<AdminUser> AdminUsers => Set<AdminUser>();
    public DbSet<Outlet> Outlets => Set<Outlet>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        var authUserIdConverter = new ValueConverter<string, Guid>(
            value => Guid.Parse(value),
            value => value.ToString());

        modelBuilder.Entity<Business>(entity =>
        {
            entity.ToTable("Business");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasColumnName("id");
            entity.Property(x => x.Name).HasColumnName("name");
            entity.Property(x => x.Slug).HasColumnName("slug");
            entity.Property(x => x.IsActive).HasColumnName("isActive");
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
            entity.Property(x => x.Id).HasColumnName("id");
            entity.Property(x => x.BusinessId).HasColumnName("businessId");
            entity.Property(x => x.Name).HasColumnName("name");
            entity.Property(x => x.IsActive).HasColumnName("isActive");
        });
    }
}
