using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Platform.Core.Domain.Entities;
using Platform.Core.Domain.Enums;
using Platform.Core.Infrastructure.Persistence.Seed;

namespace Platform.Core.Infrastructure.Persistence.Configurations;

public sealed class GlobalMaintenanceTemplateConfiguration
    : IEntityTypeConfiguration<GlobalMaintenanceTemplate>
{
    public void Configure(EntityTypeBuilder<GlobalMaintenanceTemplate> builder)
    {
        builder.ToTable("global_maintenance_templates", "pmoc");

        builder.HasKey(t => t.Id);

        builder.Property(t => t.Id)
            .ValueGeneratedNever();

        builder.Property(t => t.Name)
            .HasMaxLength(200)
            .IsRequired();

        builder.Property(t => t.Description)
            .HasMaxLength(2000);

        builder.Property(t => t.Frequency)
            .HasConversion<string>()
            .HasMaxLength(32)
            .IsRequired();

        builder.Property(t => t.Jurisdiction)
            .HasMaxLength(16)
            .IsRequired();

        builder.Property(t => t.TargetEquipmentType)
            .HasMaxLength(200)
            .IsRequired();

        builder.Property(t => t.CreatedAt)
            .IsRequired();

        builder.HasIndex(t => t.Jurisdiction);

        builder.HasIndex(t => t.Name);

        builder.HasMany(t => t.Tasks)
            .WithOne(task => task.GlobalMaintenanceTemplate)
            .HasForeignKey(task => task.GlobalMaintenanceTemplateId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.Navigation(t => t.Tasks)
            .UsePropertyAccessMode(PropertyAccessMode.Field);

        builder.HasData(
            new
            {
                Id = GlobalTemplateSeed.AnvisaNr10TemplateId,
                Name = GlobalTemplateSeed.AnvisaTemplateName,
                Description = GlobalTemplateSeed.AnvisaTemplateDescription,
                Frequency = GlobalTemplateSeed.AnvisaFrequency,
                Jurisdiction = GlobalTemplateSeed.AnvisaJurisdiction,
                TargetEquipmentType = GlobalTemplateSeed.AnvisaTargetEquipmentType,
                CreatedAt = GlobalTemplateSeed.CreatedAt,
                UpdatedAt = (DateTimeOffset?)null,
            });
    }
}
