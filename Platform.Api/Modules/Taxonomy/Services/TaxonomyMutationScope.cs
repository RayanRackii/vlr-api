using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Platform.Core.Infrastructure.Persistence;

namespace Platform.Api.Modules.Taxonomy.Services;

/// <summary>All taxonomy and consumer assignment writers acquire the same tenant lock first.</summary>
public sealed class TaxonomyMutationScope : IAsyncDisposable
{
    private readonly IDbContextTransaction? transaction;

    private TaxonomyMutationScope(IDbContextTransaction? transaction) => this.transaction = transaction;

    public static async Task<TaxonomyMutationScope> BeginAsync(AppDbContext db, Guid tenantId, CancellationToken ct)
    {
        if (!db.Database.IsRelational())
            return new(null); // InMemory unit tests; PostgreSQL integration tests exercise locking.

        var transaction = await db.Database.BeginTransactionAsync(ct);
        try
        {
            if (db.Database.IsNpgsql())
            {
                // Execute the SELECT as a command: acquire the lock without tracking a tenant entity.
                await db.Database.ExecuteSqlInterpolatedAsync(
                    $"SELECT id FROM core.tenants WHERE id = {tenantId} FOR UPDATE", ct);
            }
            return new(transaction);
        }
        catch
        {
            await transaction.DisposeAsync();
            throw;
        }
    }

    public Task CommitAsync(CancellationToken ct) => transaction?.CommitAsync(ct) ?? Task.CompletedTask;
    public ValueTask DisposeAsync() => transaction?.DisposeAsync() ?? ValueTask.CompletedTask;
}
