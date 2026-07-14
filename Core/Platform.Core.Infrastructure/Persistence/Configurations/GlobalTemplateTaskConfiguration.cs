using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Platform.Core.Domain.Entities;
using Platform.Core.Domain.Enums;
using Platform.Core.Infrastructure.Persistence.Seed;

namespace Platform.Core.Infrastructure.Persistence.Configurations;

public sealed class GlobalTemplateTaskConfiguration
    : IEntityTypeConfiguration<GlobalTemplateTask>
{
    public void Configure(EntityTypeBuilder<GlobalTemplateTask> builder)
    {
        builder.ToTable("global_template_tasks", "pmoc");

        builder.HasKey(t => t.Id);

        builder.Property(t => t.Id)
            .ValueGeneratedNever();

        builder.Property(t => t.GlobalMaintenanceTemplateId)
            .IsRequired();

        builder.Property(t => t.Title)
            .HasMaxLength(300)
            .IsRequired();

        builder.Property(t => t.InputType)
            .HasConversion<string>()
            .HasMaxLength(32)
            .IsRequired();

        builder.Property(t => t.Configuration)
            .HasColumnType("jsonb");

        builder.Property(t => t.IsMandatory)
            .HasDefaultValue(true)
            .IsRequired();

        builder.Property(t => t.Order)
            .IsRequired();

        builder.Property(t => t.CreatedAt)
            .IsRequired();

        builder.HasIndex(t => new { t.GlobalMaintenanceTemplateId, t.Order });

        builder.HasOne(t => t.GlobalMaintenanceTemplate)
            .WithMany(template => template.Tasks)
            .HasForeignKey(t => t.GlobalMaintenanceTemplateId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasData(
            new
            {
                Id = GlobalTemplateSeed.TaskPhotoLocalId,
                GlobalMaintenanceTemplateId = GlobalTemplateSeed.AnvisaNr10TemplateId,
                Title = "Foto do Local de Instalação (Visão Geral do Ambiente)",
                InputType = TaskInputType.Image,
                Configuration = (string?)null,
                IsMandatory = true,
                Order = 1,
                CreatedAt = GlobalTemplateSeed.CreatedAt,
                UpdatedAt = (DateTimeOffset?)null,
            },
            new
            {
                Id = GlobalTemplateSeed.TaskPhotoFrontId,
                GlobalMaintenanceTemplateId = GlobalTemplateSeed.AnvisaNr10TemplateId,
                Title = "Foto Frontal do Equipamento (Evaporadora)",
                InputType = TaskInputType.Image,
                Configuration = (string?)null,
                IsMandatory = true,
                Order = 2,
                CreatedAt = GlobalTemplateSeed.CreatedAt,
                UpdatedAt = (DateTimeOffset?)null,
            },
            new
            {
                Id = GlobalTemplateSeed.TaskPhotoPanelId,
                GlobalMaintenanceTemplateId = GlobalTemplateSeed.AnvisaNr10TemplateId,
                Title = "Foto do Quadro Elétrico / Painel de Comando (NR-10)",
                InputType = TaskInputType.Image,
                Configuration = (string?)null,
                IsMandatory = true,
                Order = 3,
                CreatedAt = GlobalTemplateSeed.CreatedAt,
                UpdatedAt = (DateTimeOffset?)null,
            },
            new
            {
                Id = GlobalTemplateSeed.TaskDrainCleanId,
                GlobalMaintenanceTemplateId = GlobalTemplateSeed.AnvisaNr10TemplateId,
                Title = "Limpeza da bandeja de drenagem e serpentina",
                InputType = TaskInputType.Checkbox,
                Configuration = (string?)null,
                IsMandatory = true,
                Order = 4,
                CreatedAt = GlobalTemplateSeed.CreatedAt,
                UpdatedAt = (DateTimeOffset?)null,
            },
            new
            {
                Id = GlobalTemplateSeed.TaskCurrentMeasureId,
                GlobalMaintenanceTemplateId = GlobalTemplateSeed.AnvisaNr10TemplateId,
                Title = "Medição da Corrente de Operação (Compressor)",
                InputType = TaskInputType.Number,
                Configuration = "{\"unit\":\"A\"}",
                IsMandatory = true,
                Order = 5,
                CreatedAt = GlobalTemplateSeed.CreatedAt,
                UpdatedAt = (DateTimeOffset?)null,
            },
            new
            {
                Id = GlobalTemplateSeed.TaskInsulationStateId,
                GlobalMaintenanceTemplateId = GlobalTemplateSeed.AnvisaNr10TemplateId,
                Title = "Estado do isolamento térmico",
                InputType = TaskInputType.SingleChoice,
                Configuration = "{\"options\":[\"Íntegro\",\"Danificado\",\"Inexistente\"]}",
                IsMandatory = true,
                Order = 6,
                CreatedAt = GlobalTemplateSeed.CreatedAt,
                UpdatedAt = (DateTimeOffset?)null,
            });
    }
}
