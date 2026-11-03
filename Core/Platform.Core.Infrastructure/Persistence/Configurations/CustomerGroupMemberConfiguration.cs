using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Platform.Core.Domain.Entities;

namespace Platform.Core.Infrastructure.Persistence.Configurations;

public sealed class CustomerGroupMemberConfiguration : IEntityTypeConfiguration<CustomerGroupMember>
{
    public void Configure(EntityTypeBuilder<CustomerGroupMember> builder)
    {
        builder.ToTable("customer_group_members", "catalog");

        builder.HasKey(m => new { m.TenantId, m.GroupId, m.CustomerId });
        builder.HasIndex(m => new { m.TenantId, m.CustomerId });

        builder.Property(m => m.TenantId)
            .IsRequired();

        builder.HasOne<Tenant>().WithMany()
            .HasForeignKey(m => m.TenantId)
            .OnDelete(DeleteBehavior.Restrict);

        // Removing a group removes its memberships; the customers themselves are untouched.
        builder.HasOne<CustomerGroup>().WithMany()
            .HasForeignKey(m => new { m.TenantId, m.GroupId })
            .HasPrincipalKey(g => new { g.TenantId, g.Id })
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne<Customer>().WithMany()
            .HasForeignKey(m => m.CustomerId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
