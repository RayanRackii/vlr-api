using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Platform.Core.Domain.Entities;
using Platform.Core.Domain.Enums;

namespace Platform.Core.Infrastructure.Persistence.Configurations;

public sealed class NotificationTemplateConfiguration : IEntityTypeConfiguration<NotificationTemplate>
{
    public void Configure(EntityTypeBuilder<NotificationTemplate> builder)
    {
        builder.ToTable("notification_templates", "core");

        builder.HasKey(t => t.Id);

        builder.Property(t => t.Id)
            .ValueGeneratedNever();

        builder.Property(t => t.EventType)
            .HasMaxLength(80)
            .IsRequired();

        builder.Property(t => t.Channel)
            .HasConversion<string>()
            .HasMaxLength(32)
            .IsRequired();

        builder.Property(t => t.Language)
            .HasMaxLength(16)
            .HasDefaultValue("pt-BR")
            .IsRequired();

        builder.Property(t => t.SubjectTemplate)
            .HasMaxLength(300);

        builder.Property(t => t.BodyTemplate)
            .HasColumnType("text")
            .IsRequired();

        builder.Property(t => t.WhatsAppTemplateName)
            .HasMaxLength(120);

        builder.Property(t => t.IsActive)
            .HasDefaultValue(true)
            .IsRequired();

        builder.Property(t => t.CreatedAt)
            .IsRequired();

        builder.HasIndex(t => new { t.EventType, t.Channel, t.Language })
            .IsUnique();
    }
}
