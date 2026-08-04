using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Platform.Core.Domain.Entities;

namespace Platform.Core.Infrastructure.Persistence.Configurations;

public sealed class TenantRegistrationFieldConfiguration
    : IEntityTypeConfiguration<TenantRegistrationField>
{
    public void Configure(EntityTypeBuilder<TenantRegistrationField> builder)
    {
        builder.ToTable("tenant_registration_fields", "core");

        builder.HasKey(f => f.Id);

        builder.Property(f => f.Id)
            .ValueGeneratedNever();

        builder.Property(f => f.TenantId)
            .IsRequired();

        builder.Property(f => f.FieldKey)
            .HasMaxLength(64)
            .IsRequired();

        builder.Property(f => f.Label)
            .HasMaxLength(120)
            .IsRequired();

        builder.Property(f => f.FieldType)
            .HasMaxLength(32)
            .IsRequired();

        builder.Property(f => f.IsRequired)
            .IsRequired();

        builder.Property(f => f.SortOrder)
            .IsRequired();

        builder.Property(f => f.OptionsJson)
            .HasColumnType("text");

        builder.Property(f => f.CreatedAt)
            .IsRequired();

        builder.HasIndex(f => new { f.TenantId, f.FieldKey })
            .IsUnique();

        builder.HasIndex(f => new { f.TenantId, f.SortOrder });
    }
}
