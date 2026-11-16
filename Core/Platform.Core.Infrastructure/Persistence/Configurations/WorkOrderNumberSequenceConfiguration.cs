using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Platform.Core.Domain.Entities;

namespace Platform.Core.Infrastructure.Persistence.Configurations;

public sealed class WorkOrderNumberSequenceConfiguration
    : IEntityTypeConfiguration<WorkOrderNumberSequence>
{
    public void Configure(EntityTypeBuilder<WorkOrderNumberSequence> builder)
    {
        builder.ToTable("work_order_number_sequences", "os");

        builder.HasKey(s => s.TenantId);

        builder.Property(s => s.TenantId)
            .ValueGeneratedNever();

        builder.Property(s => s.LastNumber)
            .IsRequired();
    }
}
