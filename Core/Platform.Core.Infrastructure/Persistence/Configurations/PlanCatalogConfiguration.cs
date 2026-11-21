using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Platform.Core.Domain.Entities;

namespace Platform.Core.Infrastructure.Persistence.Configurations;

public sealed class PlanCatalogConfiguration : IEntityTypeConfiguration<PlanCatalog>
{
    public void Configure(EntityTypeBuilder<PlanCatalog> builder)
    {
        builder.ToTable("plan_catalog", "billing");

        builder.HasKey(p => p.Id);

        builder.Property(p => p.Id)
            .ValueGeneratedNever();

        builder.Property(p => p.PackageKey)
            .HasMaxLength(PlanCatalog.KeyMaxLength)
            .IsRequired();

        builder.Property(p => p.PlanKey)
            .HasMaxLength(PlanCatalog.KeyMaxLength)
            .IsRequired();

        builder.Property(p => p.Name)
            .HasMaxLength(PlanCatalog.NameMaxLength)
            .IsRequired();

        builder.Property(p => p.MonthlyPriceCents)
            .IsRequired();

        builder.Property(p => p.AnnualPriceCents)
            .IsRequired();

        builder.Property(p => p.Currency)
            .HasMaxLength(3)
            .IsRequired();

        builder.Property(p => p.ModulesJson)
            .HasColumnName("modules")
            .HasColumnType("jsonb")
            .IsRequired();

        builder.Property(p => p.LimitsJson)
            .HasColumnName("limits")
            .HasColumnType("jsonb")
            .IsRequired();

        builder.Property(p => p.IsActive)
            .HasDefaultValue(true)
            .IsRequired();

        builder.Property(p => p.SortOrder)
            .IsRequired();

        builder.Property(p => p.CreatedAt)
            .IsRequired();

        builder.HasIndex(p => new { p.PackageKey, p.PlanKey })
            .IsUnique();
    }
}
