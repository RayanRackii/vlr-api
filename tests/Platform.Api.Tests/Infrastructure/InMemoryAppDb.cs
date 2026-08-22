using Microsoft.EntityFrameworkCore;
using Platform.Core.Infrastructure.Persistence;

namespace Platform.Api.Tests.Infrastructure;

internal static class InMemoryAppDb
{
    public static AppDbContext Create(ITenantProvider tenantProvider, string? databaseName = null)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(databaseName ?? $"trial-{Guid.NewGuid():N}")
            .Options;

        return new TestAppDbContext(options, tenantProvider);
    }
}
