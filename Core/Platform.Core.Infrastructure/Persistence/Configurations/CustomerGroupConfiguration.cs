using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Platform.Core.Domain.Entities;

namespace Platform.Core.Infrastructure.Persistence.Configurations;

public sealed class CustomerGroupConfiguration : IEntityTypeConfiguration<CustomerGroup>
{
    public void Configure(EntityTypeBuilder<CustomerGroup> builder)
    {
        builder.ToTable("customer_groups", "catalog");

        builder.HasKey(g => g.Id);
        builder.HasAlternateKey(g => new { g.TenantId, g.Id });

        builder.Property(g => g.Id)
            .ValueGeneratedNever();

        builder.Property(g => g.TenantId)
            .IsRequired();

        builder.Property(g => g.Name)
            .HasMaxLength(200)
            .IsRequired();

        builder.Property(g => g.Description)
            .HasColumnType("text");

        builder.Property(g => g.CreatedAt)
            .IsRequired();

        // Case-insensitive uniqueness is enforced by ux_customer_groups_tenant_lower_name (migration SQL).
        builder.HasIndex(g => new { g.TenantId, g.Name });

        builder.HasOne<Tenant>().WithMany()
            .HasForeignKey(g => g.TenantId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
