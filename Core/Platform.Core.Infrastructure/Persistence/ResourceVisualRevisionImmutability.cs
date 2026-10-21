using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Platform.Core.Domain.Entities;

namespace Platform.Core.Infrastructure.Persistence;

public static class ResourceVisualRevisionImmutability
{
    public static void Ensure(ChangeTracker tracker)
    {
        tracker.DetectChanges();
        foreach (var entry in tracker.Entries<ResourceVisualTemplateRevision>())
        {
            if (entry.State is not (EntityState.Modified or EntityState.Deleted)) continue;
            if (entry.OriginalValues.GetValue<ResourceVisualRevisionStatus>(nameof(ResourceVisualTemplateRevision.Status)) == ResourceVisualRevisionStatus.Published
                || entry.OriginalValues.GetValue<DateTimeOffset?>(nameof(ResourceVisualTemplateRevision.PublishedAt)) != null)
                throw new InvalidOperationException("Published visual revisions are immutable. Create a new revision.");
        }
    }
}
