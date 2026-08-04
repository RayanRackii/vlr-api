using Microsoft.EntityFrameworkCore;
using Platform.Api.Modules.Rentals.Dtos;
using Platform.Core.Domain.Entities;
using Platform.Core.Infrastructure.Persistence;

namespace Platform.Api.Modules.Rentals.Services;

public sealed class RentalLayoutService(
    AppDbContext dbContext,
    ITenantProvider tenantProvider) : IRentalLayoutService
{
    public async Task<IReadOnlyList<RentalLayoutResponseDto>> ListAsync(
        CancellationToken cancellationToken)
    {
        EnsureTenant();
        var layouts = await dbContext.RentalLayouts
            .AsNoTracking()
            .Include(l => l.Items)
                .ThenInclude(i => i.RentalAsset)
                    .ThenInclude(r => r.Asset)
            .OrderBy(l => l.Name)
            .ToListAsync(cancellationToken);

        return layouts.Select(ToDto).ToList();
    }

    public async Task<RentalLayoutResponseDto?> GetAsync(
        Guid id,
        CancellationToken cancellationToken)
    {
        EnsureTenant();
        var layout = await dbContext.RentalLayouts
            .AsNoTracking()
            .Include(l => l.Items)
                .ThenInclude(i => i.RentalAsset)
                    .ThenInclude(r => r.Asset)
            .FirstOrDefaultAsync(l => l.Id == id, cancellationToken);

        return layout is null ? null : ToDto(layout);
    }

    public async Task<RentalLayoutResponseDto> CreateAsync(
        UpsertRentalLayoutRequestDto request,
        CancellationToken cancellationToken)
    {
        var tenantId = EnsureTenant();
        var layout = new RentalLayout
        {
            TenantId = tenantId,
            UnitId = request.UnitId,
            Name = request.Name.Trim(),
            IsActive = request.IsActive,
        };

        foreach (var item in request.Items)
        {
            await EnsureRentableAsync(item.RentalAssetId, cancellationToken);
            ValidatePercents(item);
            layout.AddItem(new RentalLayoutItem
            {
                TenantId = tenantId,
                LayoutId = layout.Id,
                RentalAssetId = item.RentalAssetId,
                XPercent = item.XPercent,
                YPercent = item.YPercent,
                WidthPercent = item.WidthPercent,
                HeightPercent = item.HeightPercent,
                ZIndex = item.ZIndex,
            });
        }

        dbContext.RentalLayouts.Add(layout);
        await dbContext.SaveChangesAsync(cancellationToken);
        return (await GetAsync(layout.Id, cancellationToken))!;
    }

    public async Task<RentalLayoutResponseDto> UpdateAsync(
        Guid id,
        UpsertRentalLayoutRequestDto request,
        CancellationToken cancellationToken)
    {
        var tenantId = EnsureTenant();
        var layout = await dbContext.RentalLayouts
            .Include(l => l.Items)
            .FirstOrDefaultAsync(l => l.Id == id, cancellationToken)
            ?? throw new KeyNotFoundException("Layout was not found.");

        layout.UnitId = request.UnitId;
        layout.Name = request.Name.Trim();
        layout.IsActive = request.IsActive;
        layout.Touch();

        dbContext.RentalLayoutItems.RemoveRange(layout.Items);

        foreach (var item in request.Items)
        {
            await EnsureRentableAsync(item.RentalAssetId, cancellationToken);
            ValidatePercents(item);
            dbContext.RentalLayoutItems.Add(new RentalLayoutItem
            {
                TenantId = tenantId,
                LayoutId = layout.Id,
                RentalAssetId = item.RentalAssetId,
                XPercent = item.XPercent,
                YPercent = item.YPercent,
                WidthPercent = item.WidthPercent,
                HeightPercent = item.HeightPercent,
                ZIndex = item.ZIndex,
            });
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        return (await GetAsync(layout.Id, cancellationToken))!;
    }

    private async Task EnsureRentableAsync(Guid rentalAssetId, CancellationToken cancellationToken)
    {
        var ok = await dbContext.RentalAssets.AnyAsync(r => r.Id == rentalAssetId && r.IsActive, cancellationToken);
        if (!ok)
        {
            throw new KeyNotFoundException($"Rentable '{rentalAssetId}' was not found.");
        }
    }

    private static void ValidatePercents(UpsertRentalLayoutItemRequestDto item)
    {
        if (item.WidthPercent <= 0 || item.HeightPercent <= 0
            || item.XPercent < 0 || item.YPercent < 0
            || item.XPercent + item.WidthPercent > 100.0001
            || item.YPercent + item.HeightPercent > 100.0001)
        {
            throw new ArgumentException("Layout item percents must fit within the 0–100 canvas.");
        }
    }

    private Guid EnsureTenant() =>
        tenantProvider.TenantId
        ?? throw new UnauthorizedAccessException("Tenant context is required.");

    private static RentalLayoutResponseDto ToDto(RentalLayout layout) =>
        new(
            layout.Id,
            layout.UnitId,
            layout.Name,
            layout.IsActive,
            layout.Items
                .OrderBy(i => i.ZIndex)
                .Select(i => new RentalLayoutItemResponseDto(
                    i.Id,
                    i.RentalAssetId,
                    i.RentalAsset.Asset.Name,
                    i.XPercent,
                    i.YPercent,
                    i.WidthPercent,
                    i.HeightPercent,
                    i.ZIndex))
                .ToList());
}
