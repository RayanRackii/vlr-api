using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Platform.Core.Domain.Entities;
using Platform.Core.Domain.Enums;

namespace Platform.Core.Infrastructure.Persistence.Configurations;

public sealed class AssetConfiguration : IEntityTypeConfiguration<Asset>
{
    public void Configure(EntityTypeBuilder<Asset> builder)
    {
        builder.ToTable("assets", "assets");

        builder.HasKey(a => a.Id);

        builder.Property(a => a.Id)
            .ValueGeneratedNever();

        builder.Property(a => a.TenantId)
            .IsRequired();

        builder.Property(a => a.UnitId)
            .IsRequired();

        builder.Property(a => a.CategoryId)
            .IsRequired();

        builder.Property(a => a.FamilyId)
            .IsRequired();

        builder.Property(a => a.Name)
            .HasMaxLength(200)
            .IsRequired();

        builder.Property(a => a.Tag)
            .HasMaxLength(100)
            .IsRequired();

        builder.Property(a => a.Location)
            .HasMaxLength(300);

        builder.Property(a => a.SerialNumber)
            .HasMaxLength(150);

        builder.Property(a => a.Status)
            .HasConversion<string>()
            .HasMaxLength(32)
            .HasDefaultValue(AssetStatus.Active)
            .IsRequired();

        builder.Property(a => a.IsRentable)
            .HasDefaultValue(false)
            .IsRequired();

        builder.Property(a => a.RequiresMaintenance)
            .HasDefaultValue(false)
            .IsRequired();

        builder.Property(a => a.Attributes)
            .HasConversion<AssetAttributesConverter, AssetAttributesComparer>()
            .HasColumnType("jsonb")
            .HasDefaultValueSql("'{}'::jsonb")
            .IsRequired();

        builder.Property(a => a.CreatedAt)
            .IsRequired();

        builder.Property(a => a.ScheduledDeletionAt);

        builder.HasIndex(a => new { a.TenantId, a.Tag })
            .IsUnique();

        builder.HasIndex(a => new { a.TenantId, a.UnitId });

        builder.HasIndex(a => new { a.TenantId, a.CategoryId });

        builder.HasIndex(a => new { a.TenantId, a.FamilyId });

        builder.HasIndex(a => new { a.TenantId, a.IsRentable });

        builder.HasIndex(a => a.ScheduledDeletionAt);

        builder.HasOne(a => a.Unit)
            .WithMany()
            .HasForeignKey(a => a.UnitId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(a => a.Category)
            .WithMany(c => c.Assets)
            .HasForeignKey(a => a.CategoryId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(a => a.Family)
            .WithMany()
            .HasForeignKey(a => a.FamilyId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(a => a.RentalConfiguration)
            .WithOne(r => r.Asset)
            .HasForeignKey<RentalAsset>(r => r.AssetId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

/// <summary>Helpers shared by the asset attributes converter and comparer (expression-tree friendly).</summary>
internal static class AssetAttributesJson
{
    public static string Serialize(Dictionary<string, JsonElement>? value)
    {
        if (value is null || value.Count == 0)
        {
            return "{}";
        }

        return JsonSerializer.Serialize(new SortedDictionary<string, JsonElement>(value, StringComparer.Ordinal));
    }

    public static Dictionary<string, JsonElement> Deserialize(string? value)
    {
        var result = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(value))
        {
            return result;
        }

        var parsed = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(value);
        if (parsed is null)
        {
            return result;
        }

        foreach (var (key, element) in parsed)
        {
            if (element.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            {
                continue;
            }

            result[key] = element;
        }

        return result;
    }

    public static bool AreEqual(Dictionary<string, JsonElement>? left, Dictionary<string, JsonElement>? right) =>
        string.Equals(Serialize(left), Serialize(right), StringComparison.Ordinal);

    public static int Hash(Dictionary<string, JsonElement>? value) =>
        Serialize(value).GetHashCode(StringComparison.Ordinal);

    public static Dictionary<string, JsonElement> Snapshot(Dictionary<string, JsonElement>? value) =>
        value is null
            ? new Dictionary<string, JsonElement>(StringComparer.Ordinal)
            : new Dictionary<string, JsonElement>(value, StringComparer.Ordinal);
}

public sealed class AssetAttributesConverter : ValueConverter<Dictionary<string, JsonElement>, string>
{
    public AssetAttributesConverter()
        : base(
            value => AssetAttributesJson.Serialize(value),
            value => AssetAttributesJson.Deserialize(value))
    {
    }
}

public sealed class AssetAttributesComparer : ValueComparer<Dictionary<string, JsonElement>>
{
    public AssetAttributesComparer()
        : base(
            (left, right) => AssetAttributesJson.AreEqual(left, right),
            value => AssetAttributesJson.Hash(value),
            value => AssetAttributesJson.Snapshot(value))
    {
    }
}
