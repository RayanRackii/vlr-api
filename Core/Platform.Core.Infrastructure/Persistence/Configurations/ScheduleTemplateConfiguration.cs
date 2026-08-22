using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Platform.Core.Domain.Entities;

namespace Platform.Core.Infrastructure.Persistence.Configurations;

public sealed class ScheduleTemplateConfiguration : IEntityTypeConfiguration<ScheduleTemplate>
{
    public void Configure(EntityTypeBuilder<ScheduleTemplate> builder)
    {
        builder.ToTable("schedule_templates", "rentals");

        builder.HasKey(t => t.Id);
        builder.Property(t => t.Id).ValueGeneratedNever();
        builder.Property(t => t.TenantId).IsRequired();
        builder.Property(t => t.RentalAssetId).IsRequired();
        builder.Property(t => t.DayOfWeek).IsRequired();
        builder.Property(t => t.StartTime).IsRequired();
        builder.Property(t => t.EndTime).IsRequired();
        builder.Property(t => t.OccupancyKindId).IsRequired();
        builder.Property(t => t.Label).HasMaxLength(200);
        builder.Property(t => t.IsActive).HasDefaultValue(true).IsRequired();
        builder.Property(t => t.CreatedAt).IsRequired();

        builder.HasIndex(t => new { t.TenantId, t.RentalAssetId, t.DayOfWeek, t.StartTime });

        builder.HasIndex(t => new
            {
                t.TenantId,
                t.RentalAssetId,
                t.DayOfWeek,
                t.StartTime,
                t.EndTime,
                t.OccupancyKindId,
            })
            .IsUnique()
            .HasDatabaseName("ix_schedule_templates_exact_duplicate");

        builder.HasOne(t => t.RentalAsset)
            .WithMany()
            .HasForeignKey(t => t.RentalAssetId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(t => t.OccupancyKind)
            .WithMany()
            .HasForeignKey(t => t.OccupancyKindId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
