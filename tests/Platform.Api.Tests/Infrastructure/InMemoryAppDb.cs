using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Options;
using Platform.Core.Infrastructure.Persistence;

namespace Platform.Api.Tests.Infrastructure;

internal static class InMemoryAppDb
{
    public static AppDbContext Create(
        ITenantProvider tenantProvider,
        string? databaseName = null,
        bool ignoreInMemoryTransactions = false,
        IEnumerable<IInterceptor>? interceptors = null,
        bool failClosed = false)
    {
        var builder = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(databaseName ?? $"trial-{Guid.NewGuid():N}");

        if (ignoreInMemoryTransactions)
        {
            builder.ConfigureWarnings(warnings =>
                warnings.Ignore(InMemoryEventId.TransactionIgnoredWarning));
        }

        if (interceptors is not null)
        {
            builder.AddInterceptors(interceptors);
        }

        return new TestAppDbContext(
            builder.Options,
            tenantProvider,
            Options.Create(new TenancyOptions { FailClosed = failClosed }));
    }
}
