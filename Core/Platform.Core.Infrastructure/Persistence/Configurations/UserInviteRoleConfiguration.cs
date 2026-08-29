using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Platform.Core.Domain.Entities;

namespace Platform.Core.Infrastructure.Persistence.Configurations;

public sealed class UserInviteRoleConfiguration : IEntityTypeConfiguration<UserInviteRole>
{
    public void Configure(EntityTypeBuilder<UserInviteRole> builder)
    {
        builder.ToTable("user_invite_roles");

        builder.HasKey(ir => new { ir.InviteId, ir.RoleId });

        builder.HasOne(ir => ir.Invite)
            .WithMany(i => i.InviteRoles)
            .HasForeignKey(ir => ir.InviteId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(ir => ir.Role)
            .WithMany()
            .HasForeignKey(ir => ir.RoleId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
