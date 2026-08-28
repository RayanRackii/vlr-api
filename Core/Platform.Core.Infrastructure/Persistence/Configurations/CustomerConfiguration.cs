using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Platform.Core.Domain.Entities;
using Platform.Core.Domain.Enums;

namespace Platform.Core.Infrastructure.Persistence.Configurations;

public sealed class CustomerConfiguration : IEntityTypeConfiguration<Customer>
{
    public void Configure(EntityTypeBuilder<Customer> builder)
    {
        builder.ToTable("customers", "core");

        builder.HasKey(c => c.Id);

        builder.Property(c => c.Id)
            .ValueGeneratedNever();

        builder.Property(c => c.TenantId)
            .IsRequired();

        builder.Property(c => c.Name)
            .HasMaxLength(200)
            .IsRequired();

        builder.Property(c => c.Phone)
            .HasMaxLength(32);

        builder.Property(c => c.Email)
            .HasMaxLength(256);

        builder.Property(c => c.PasswordHash)
            .HasMaxLength(500);

        builder.Property(c => c.CustomerType)
            .HasConversion<int>()
            .HasDefaultValue(CustomerType.Individual)
            .IsRequired();

        builder.Property(c => c.Cpf)
            .HasMaxLength(11);

        builder.Property(c => c.Document)
            .HasMaxLength(14);

        builder.Property(c => c.PostalCode)
            .HasMaxLength(8);

        builder.Property(c => c.AddressStreet)
            .HasMaxLength(200);

        builder.Property(c => c.AddressNeighborhood)
            .HasMaxLength(120);

        builder.Property(c => c.AddressCity)
            .HasMaxLength(120);

        builder.Property(c => c.AddressState)
            .HasMaxLength(2);

        builder.Property(c => c.PhotoUrl)
            .HasColumnType("text");

        builder.Property(c => c.ExtraAttributes)
            .HasColumnType("jsonb")
            .HasDefaultValueSql("'{}'::jsonb")
            .IsRequired();

        builder.Property(c => c.PhoneVerifiedAt);

        builder.Property(c => c.LastLoginAt);

        builder.Property(c => c.CreatedAt)
            .IsRequired();

        builder.HasIndex(c => new { c.TenantId, c.Name });

        builder.HasIndex(c => new { c.TenantId, c.LastLoginAt });

        builder.HasIndex(c => new { c.TenantId, c.Phone })
            .IsUnique()
            .HasFilter("phone IS NOT NULL");

        builder.HasIndex(c => new { c.TenantId, c.Email })
            .IsUnique()
            .HasFilter("email IS NOT NULL");

        builder.HasIndex(c => new { c.TenantId, c.Cpf })
            .IsUnique()
            .HasFilter("cpf IS NOT NULL");

        builder.HasIndex(c => new { c.TenantId, c.Document })
            .IsUnique()
            .HasFilter("document IS NOT NULL");

        builder.HasMany(c => c.OtpCodes)
            .WithOne(o => o.Customer)
            .HasForeignKey(o => o.CustomerId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.Navigation(c => c.OtpCodes)
            .UsePropertyAccessMode(PropertyAccessMode.Field);
    }
}
