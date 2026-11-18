using Microsoft.EntityFrameworkCore;
using Platform.Core.Domain.Entities;
using Platform.Core.Infrastructure.Persistence;

namespace Platform.Api.Tests.Infrastructure;

/// <summary>
/// Inserts a tenant with raw SQL listing only columns that exist at old migrations. Tests that migrate
/// to an older step must not insert a <see cref="Tenant"/> through EF: the current model also maps
/// columns added later (contact and address), which do not exist yet.
/// </summary>
internal static class LegacyTenantSql
{
    public static Task InsertAsync(AppDbContext db, Tenant tenant) =>
        db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO core.tenants (id, legal_name, trade_name, tax_id, subdomain, created_at)
            VALUES ({tenant.Id}, {tenant.LegalName}, {tenant.TradeName}, {tenant.TaxId}, {tenant.Subdomain}, now())
            """);
}
