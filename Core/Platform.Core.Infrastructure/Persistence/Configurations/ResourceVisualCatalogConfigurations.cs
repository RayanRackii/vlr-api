using System.Text.Json;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Platform.Core.Domain.Entities;

namespace Platform.Core.Infrastructure.Persistence.Configurations;

public sealed class ResourceVisualTemplateConfiguration : IEntityTypeConfiguration<ResourceVisualTemplate>
{
    public void Configure(EntityTypeBuilder<ResourceVisualTemplate> b)
    {
        b.ToTable("resource_visual_templates", "core"); b.HasKey(x => x.Id); b.Property(x => x.Id).ValueGeneratedNever();
        b.Property(x => x.Slug).HasMaxLength(80).IsRequired(); b.HasIndex(x => x.Slug).IsUnique();
        b.Property(x => x.NameI18n).HasColumnType("jsonb").IsRequired(); b.Property(x => x.DescriptionI18n).HasColumnType("jsonb").IsRequired();
        b.Property(x => x.Version).IsConcurrencyToken();
    }
}
public sealed class ResourceVisualTemplateRevisionConfiguration : IEntityTypeConfiguration<ResourceVisualTemplateRevision>
{
    private static readonly JsonSerializerOptions DefaultPricingsJsonOptions = new();

    public void Configure(EntityTypeBuilder<ResourceVisualTemplateRevision> b)
    {
        b.ToTable("resource_visual_template_revisions", "core"); b.HasKey(x => x.Id); b.Property(x => x.Id).ValueGeneratedNever();
        b.HasOne<ResourceVisualTemplate>().WithMany().HasForeignKey(x => x.TemplateId).OnDelete(DeleteBehavior.Restrict);
        b.Property(x => x.Status).HasConversion<string>().HasMaxLength(16); b.Property(x => x.FloorSvg).IsRequired();
        b.Property(x => x.ViewBox).HasMaxLength(100); b.Property(x => x.AspectRatio).HasPrecision(12, 6);
        b.Property(x => x.DefaultColor).HasMaxLength(7); b.Property(x => x.DefaultIcon).HasMaxLength(80); b.Property(x => x.ArtSha256).HasMaxLength(64);
        b.Property(x => x.DefaultSchedulePolicy).HasConversion<string>().HasMaxLength(32);
        b.Property(x => x.DefaultAllowedDurationMinutes).HasMaxLength(128);
        b.Property(x => x.DefaultPricings)
            .HasConversion<ResourceVisualDefaultPricingsConverter, ResourceVisualDefaultPricingsComparer>()
            .HasColumnType("jsonb");
        b.Property(x => x.Version).IsConcurrencyToken();
        b.HasIndex(x => new { x.TemplateId, x.RevisionNo }).IsUnique();
        b.HasIndex(x => x.TemplateId).IsUnique().HasFilter("status = 'Draft'");
        b.HasIndex(x => new { x.TemplateId, x.Status, x.RevisionNo });
    }

    internal static string? SerializeDefaultPricings(List<ResourceVisualDefaultPricing>? value) =>
        value is null ? null : JsonSerializer.Serialize(value, DefaultPricingsJsonOptions);

    internal static List<ResourceVisualDefaultPricing>? DeserializeDefaultPricings(string? value) =>
        value is null ? null : JsonSerializer.Deserialize<List<ResourceVisualDefaultPricing>>(value, DefaultPricingsJsonOptions);

    internal static int GetDefaultPricingsHashCode(List<ResourceVisualDefaultPricing>? value) =>
        SerializeDefaultPricings(value)?.GetHashCode(StringComparison.Ordinal) ?? 0;
}

public sealed class ResourceVisualDefaultPricingsConverter : ValueConverter<List<ResourceVisualDefaultPricing>?, string?>
{
    public ResourceVisualDefaultPricingsConverter()
        : base(
            value => ResourceVisualTemplateRevisionConfiguration.SerializeDefaultPricings(value),
            value => ResourceVisualTemplateRevisionConfiguration.DeserializeDefaultPricings(value))
    {
    }
}

public sealed class ResourceVisualDefaultPricingsComparer : ValueComparer<List<ResourceVisualDefaultPricing>?>
{
    public ResourceVisualDefaultPricingsComparer()
        : base(
            (left, right) => ResourceVisualTemplateRevisionConfiguration.SerializeDefaultPricings(left) == ResourceVisualTemplateRevisionConfiguration.SerializeDefaultPricings(right),
            value => ResourceVisualTemplateRevisionConfiguration.GetDefaultPricingsHashCode(value),
            value => ResourceVisualTemplateRevisionConfiguration.DeserializeDefaultPricings(ResourceVisualTemplateRevisionConfiguration.SerializeDefaultPricings(value)))
    {
    }
}
public sealed class ResourceVisualTagConfiguration : IEntityTypeConfiguration<ResourceVisualTag>
{
    public void Configure(EntityTypeBuilder<ResourceVisualTag> b)
    {
        b.ToTable("resource_visual_tags", "core"); b.HasKey(x => x.Id); b.Property(x => x.Id).ValueGeneratedNever();
        b.HasAlternateKey(x => new { x.Id, x.Kind }); b.Property(x => x.Kind).HasConversion<string>().HasMaxLength(16);
        b.Property(x => x.Slug).HasMaxLength(80); b.HasIndex(x => x.Slug).IsUnique(); b.Property(x => x.Facet).HasMaxLength(32);
        b.Property(x => x.NameI18n).HasColumnType("jsonb").IsRequired();
    }
}
public sealed class ResourceVisualTemplateTagConfiguration : IEntityTypeConfiguration<ResourceVisualTemplateTag>
{
    public void Configure(EntityTypeBuilder<ResourceVisualTemplateTag> b)
    {
        b.ToTable("resource_visual_template_tags", "core"); b.HasKey(x => new { x.TemplateId, x.TagId });
        b.HasOne<ResourceVisualTemplate>().WithMany().HasForeignKey(x => x.TemplateId).OnDelete(DeleteBehavior.Cascade);
        b.HasOne<ResourceVisualTag>().WithMany().HasForeignKey(x => x.TagId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => new { x.TagId, x.TemplateId });
    }
}
public sealed class TenantResourceSegmentConfiguration : IEntityTypeConfiguration<TenantResourceSegment>
{
    public void Configure(EntityTypeBuilder<TenantResourceSegment> b)
    {
        b.ToTable("tenant_resource_segments", "core", t => t.HasCheckConstraint("ck_tenant_resource_segments_segment", "tag_kind = 'Segment'"));
        b.HasKey(x => new { x.TenantId, x.TagId }); b.Property(x => x.TagKind).HasConversion<string>().HasMaxLength(16);
        b.HasOne<Tenant>().WithMany().HasForeignKey(x => x.TenantId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<ResourceVisualTag>().WithMany().HasForeignKey(x => new { x.TagId, x.TagKind }).HasPrincipalKey(x => new { x.Id, x.Kind }).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => new { x.TagId, x.TenantId });
    }
}
