using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Platform.Core.Domain.Entities;

namespace Platform.Core.Infrastructure.Persistence.Configurations;

public sealed class TrialSignupClaimConfiguration : IEntityTypeConfiguration<TrialSignupClaim>
{
    public void Configure(EntityTypeBuilder<TrialSignupClaim> builder)
    {
        builder.ToTable("trial_signup_claims", "core");

        builder.HasKey(c => c.Id);

        builder.Property(c => c.Id)
            .ValueGeneratedNever();

        builder.Property(c => c.EmailNormalized)
            .HasMaxLength(320)
            .IsRequired();

        builder.Property(c => c.PhoneNormalized)
            .HasMaxLength(32)
            .IsRequired();

        builder.Property(c => c.TenantId);

        builder.Property(c => c.CreatedAt)
            .IsRequired();

        builder.HasIndex(c => c.EmailNormalized)
            .IsUnique();

        builder.HasIndex(c => c.PhoneNormalized)
            .IsUnique();
    }
}
