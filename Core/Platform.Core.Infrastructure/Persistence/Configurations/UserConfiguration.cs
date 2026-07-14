using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Platform.Core.Domain.Entities;

namespace Platform.Core.Infrastructure.Persistence.Configurations;

public sealed class UserConfiguration : IEntityTypeConfiguration<User>
{
    public void Configure(EntityTypeBuilder<User> builder)
    {
        builder.ToTable("users");

        builder.HasKey(u => u.Id);

        builder.Property(u => u.Id)
            .ValueGeneratedNever();

        builder.Property(u => u.TenantId)
            .IsRequired();

        builder.Property(u => u.SupabaseAuthId)
            .HasMaxLength(36)
            .IsRequired();

        builder.Property(u => u.FullName)
            .HasMaxLength(200)
            .IsRequired();

        builder.Property(u => u.Email)
            .HasMaxLength(320)
            .IsRequired();

        builder.Property(u => u.IsActive)
            .HasDefaultValue(true)
            .IsRequired();

        builder.Property(u => u.CreatedAt)
            .IsRequired();

        builder.HasIndex(u => u.SupabaseAuthId)
            .IsUnique();

        builder.HasIndex(u => new { u.TenantId, u.Email })
            .IsUnique();

        builder.HasMany(u => u.UserRoles)
            .WithOne(ur => ur.User)
            .HasForeignKey(ur => ur.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.Navigation(u => u.UserRoles)
            .UsePropertyAccessMode(PropertyAccessMode.Field);
    }
}
