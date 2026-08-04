using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Platform.Core.Domain.Entities;

namespace Platform.Core.Infrastructure.Persistence.Configurations;

public sealed class UserInviteConfiguration : IEntityTypeConfiguration<UserInvite>
{
    public void Configure(EntityTypeBuilder<UserInvite> builder)
    {
        builder.ToTable("user_invites", "core");

        builder.HasKey(i => i.Id);
        builder.Property(i => i.Id).ValueGeneratedNever();
        builder.Property(i => i.TenantId).IsRequired();
        builder.Property(i => i.Email).HasMaxLength(320).IsRequired();
        builder.Property(i => i.FullName).HasMaxLength(200).IsRequired();
        builder.Property(i => i.RoleName).HasMaxLength(64).IsRequired();
        builder.Property(i => i.Token).HasMaxLength(64).IsRequired();
        builder.Property(i => i.ExpiresAt).IsRequired();
        builder.Property(i => i.CreatedAt).IsRequired();

        builder.HasIndex(i => i.Token).IsUnique();
        builder.HasIndex(i => new { i.TenantId, i.Email, i.AcceptedAt });

        builder.HasOne(i => i.Tenant)
            .WithMany()
            .HasForeignKey(i => i.TenantId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
