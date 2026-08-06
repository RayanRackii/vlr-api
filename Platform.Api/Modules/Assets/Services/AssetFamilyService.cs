using Microsoft.EntityFrameworkCore;
using Platform.Api.Modules.Assets.Dtos;
using Platform.Core.Infrastructure.Persistence;

namespace Platform.Api.Modules.Assets.Services;

public interface IAssetFamilyService
{
    Task<IReadOnlyList<AssetFamilyDetailResponse>> ListCatalogAsync(
        CancellationToken cancellationToken);

    Task<IReadOnlyList<AssetFamilyDetailResponse>> ListActiveForTenantAsync(
        Guid tenantId,
        CancellationToken cancellationToken);
}

public sealed class AssetFamilyService(AppDbContext dbContext) : IAssetFamilyService
{
    public async Task<IReadOnlyList<AssetFamilyDetailResponse>> ListCatalogAsync(
        CancellationToken cancellationToken)
    {
        var families = await dbContext.AssetFamilies
            .AsNoTracking()
            .Where(f => f.IsActive)
            .OrderBy(f => f.SortOrder)
            .ThenBy(f => f.Label)
            .ToListAsync(cancellationToken);

        return families.Select(ToDetail).ToList();
    }

    public async Task<IReadOnlyList<AssetFamilyDetailResponse>> ListActiveForTenantAsync(
        Guid tenantId,
        CancellationToken cancellationToken)
    {
        var families = await dbContext.TenantAssetFamilies
            .AsNoTracking()
            .IgnoreQueryFilters()
            .Where(t => t.TenantId == tenantId)
            .Join(
                dbContext.AssetFamilies.AsNoTracking().Where(f => f.IsActive),
                t => t.FamilyId,
                f => f.Id,
                (_, f) => f)
            .OrderBy(f => f.SortOrder)
            .ThenBy(f => f.Label)
            .ToListAsync(cancellationToken);

        return families.Select(ToDetail).ToList();
    }

    private static AssetFamilyDetailResponse ToDetail(Platform.Core.Domain.Entities.AssetFamily family)
    {
        var schema = AssetFamilyAttributeValidator.ParseSchema(family.FieldSchemaJson);
        var fields = schema.Fields
            .Select(f => new AssetFamilyFieldDto(f.Key, f.Type, f.Required, f.Label))
            .ToList();

        return new AssetFamilyDetailResponse(
            family.Id,
            family.Key,
            family.Label,
            fields,
            family.SortOrder,
            family.IsActive);
    }
}
