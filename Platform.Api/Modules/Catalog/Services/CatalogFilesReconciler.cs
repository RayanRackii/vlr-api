using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Platform.Api.Storage;
using Platform.Core.Domain.Entities;
using Platform.Core.Domain.Enums;
using Platform.Core.Infrastructure.Persistence;

namespace Platform.Api.Modules.Catalog.Services;

/// <summary>Operator-only reconciliation; public storage is the source of truth, never current visibility alone.</summary>
public sealed class CatalogFilesReconciler(AppDbContext db, IStorageProvider storage, IOptions<StorageOptions> options)
{
    public async Task<int> RunAsync(TextWriter output, bool apply = false, CancellationToken ct = default)
    {
        var publicKeys = (await storage.ListAsync(options.Value.PublicBucket, "", ct)).ToHashSet(StringComparer.Ordinal);
        var counts = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["legacy-ok"] = 0, ["restricted-public"] = 0, ["internal-public"] = 0,
            ["orphan"] = 0, ["flag-mismatch"] = 0, ["skipped"] = 0
        };
        var changes = 0;
        foreach (var key in publicKeys.Order(StringComparer.Ordinal))
        {
            var parts = key.Split('/');
            // Explicit WorkOrder namespace is not Catalog. Unknown/malformed layouts are
            // orphans rather than silently allowing possibly exposed catalog objects.
            if (parts.Length > 1 && Guid.TryParseExact(parts[0], "N", out _) && parts[1] == "work-orders")
            {
                await LogAsync(key, null, null, null, "skipped", "none");
                continue;
            }
            if (parts.Length != 3 || !Guid.TryParseExact(parts[0], "N", out var tenant)
                || !Guid.TryParseExact(parts[1], "N", out var product) || !Guid.TryParseExact(parts[2], "N", out var file))
            {
                // A malformed file component can still identify a product: serialize its
                // removal with ordinary product writes too.
                if (parts.Length >= 2 && Guid.TryParseExact(parts[0], "N", out var keyTenant)
                    && Guid.TryParseExact(parts[1], "N", out var keyProduct))
                    changes += await ReconcileAsync(key, keyTenant, keyProduct, Guid.Empty, publicPresent: true);
                else
                {
                    if (apply) await storage.DeleteRequiredAsync(options.Value.PublicBucket, key, ct);
                    changes += apply ? 1 : 0;
                    await LogAsync(key, null, null, null, "orphan", "delete-public");
                }
                continue;
            }
            changes += await ReconcileAsync(key, tenant, product, file, publicPresent: true);
        }

        // Discovery is intentionally cross-tenant for this CLI only; every mutation below
        // reselects by tenant_id and takes the same product lock as normal catalog writes.
        var flagged = await db.CatalogProductFiles.IgnoreQueryFilters().AsNoTracking()
            .Where(f => f.IsLegacyPublic).Select(f => new { f.TenantId, f.ProductId, f.Id, f.StorageKey }).ToListAsync(ct);
        foreach (var f in flagged.Where(f => !publicKeys.Contains(f.StorageKey)))
            changes += await ReconcileAsync(f.StorageKey, f.TenantId, f.ProductId, f.Id, publicPresent: false);

        foreach (var (classification, count) in counts)
            await output.WriteLineAsync($"summary class={classification} count={count}");
        await output.WriteLineAsync($"summary mode={(apply ? "apply" : "report-only")} changes={changes}");
        return changes;

        async Task LogAsync(string key, Guid? tenant, Guid? product, Guid? file, string classification, string action)
        {
            counts[classification]++;
            // JSON escaping keeps each object on one log line, even with a malformed key.
            await output.WriteLineAsync($"key={System.Text.Json.JsonSerializer.Serialize(key)} tenant={tenant:N} product={product:N} file={file:N} class={classification} action={(apply ? "" : "would-")}{action}");
        }

        async Task<int> ReconcileAsync(string key, Guid tenant, Guid product, Guid file, bool publicPresent)
        {
            await using var transaction = db.Database.IsRelational()
                ? await db.Database.BeginTransactionAsync(ct) : null;
            if (db.Database.IsRelational())
                await db.Database.ExecuteSqlInterpolatedAsync(
                    $"SELECT 1 FROM catalog.catalog_products WHERE tenant_id = {tenant} AND id = {product} FOR UPDATE", ct);
            var owner = await db.CatalogProducts.IgnoreQueryFilters().AsNoTracking()
                .SingleOrDefaultAsync(p => p.TenantId == tenant && p.Id == product, ct);
            var row = await db.CatalogProductFiles.IgnoreQueryFilters()
                .SingleOrDefaultAsync(f => f.TenantId == tenant && f.ProductId == product && f.Id == file && f.StorageKey == key, ct);
            if (row is not null && db.Database.IsRelational()) await db.Entry(row).ReloadAsync(ct);
            var classification = !publicPresent ? "flag-mismatch"
                : owner is null || row is null ? "orphan"
                : owner.Visibility == CatalogProductVisibility.Restricted ? "restricted-public"
                : row.Visibility != CatalogFileVisibility.CustomerVisible ? "internal-public"
                : "legacy-ok";
            var delete = publicPresent && classification != "legacy-ok";
            var legacy = classification == "legacy-ok";
            var missingPrivate = false;
            if (row is not null && !legacy && (classification is "restricted-public" or "internal-public"
                || (!publicPresent && (owner?.Visibility == CatalogProductVisibility.Restricted
                    || row.Visibility != CatalogFileVisibility.CustomerVisible))))
            {
                var privateKeys = await storage.ListAsync(options.Value.PrivateBucket, $"{tenant:N}/{product:N}/", ct);
                missingPrivate = !privateKeys.Contains(key, StringComparer.Ordinal);
            }
            var rowChanged = row is not null && (row.IsLegacyPublic != legacy
                || (missingPrivate && row.Status != CatalogFileStatus.Failed));
            if (apply)
            {
                if (rowChanged)
                {
                    row!.IsLegacyPublic = legacy;
                    if (missingPrivate) row.Status = CatalogFileStatus.Failed;
                    await db.SaveChangesAsync(ct);
                }
                if (delete) await storage.DeleteRequiredAsync(options.Value.PublicBucket, key, ct);
                if (transaction is not null) await transaction.CommitAsync(ct);
            }
            var action = delete ? "delete-public" : rowChanged ? (legacy ? "set-legacy" : "clear-legacy") : "none";
            if (missingPrivate) action += ",missing-private,mark-failed";
            await LogAsync(key, tenant, product, file, classification, action);
            return apply && (rowChanged || delete) ? 1 : 0;
        }
    }
}
