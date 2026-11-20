using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Platform.Core.Domain.Entities;

namespace Platform.Core.Infrastructure.Persistence.Configurations;

public sealed class IdempotencyKeyConfiguration : IEntityTypeConfiguration<IdempotencyKey>
{
    public void Configure(EntityTypeBuilder<IdempotencyKey> builder)
    {
        builder.ToTable("idempotency_keys", "core");

        builder.HasKey(k => k.Id);

        builder.Property(k => k.Id)
            .ValueGeneratedNever();

        builder.Property(k => k.TenantId)
            .IsRequired();

        builder.Property(k => k.Scope)
            .HasMaxLength(IdempotencyKey.ScopeMaxLength)
            .IsRequired();

        builder.Property(k => k.PrincipalId)
            .IsRequired();

        builder.Property(k => k.Key)
            .HasMaxLength(IdempotencyKey.KeyMaxLength)
            .IsRequired();

        builder.Property(k => k.RequestHash)
            .HasMaxLength(IdempotencyKey.RequestHashLength)
            .IsRequired();

        builder.Property(k => k.ExpiresAt)
            .IsRequired();

        builder.Property(k => k.CreatedAt)
            .IsRequired();

        // The row is never updated after completion bookkeeping; the spec table has no updated_at.
        builder.Ignore(k => k.UpdatedAt);

        builder.HasOne<Tenant>().WithMany().HasForeignKey(k => k.TenantId).OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(k => new { k.TenantId, k.Scope, k.PrincipalId, k.Key })
            .IsUnique();

        builder.HasIndex(k => k.ExpiresAt);
    }
}
