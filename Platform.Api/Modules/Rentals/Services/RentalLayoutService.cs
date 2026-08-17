using Microsoft.EntityFrameworkCore;
using Platform.Api.Modules.Rentals.Dtos;
using Platform.Api.Services.Trial;
using Platform.Core.Domain.Entities;
using Platform.Core.Infrastructure.Persistence;

namespace Platform.Api.Modules.Rentals.Services;

public sealed class RentalLayoutService(
    AppDbContext dbContext,
    ITenantProvider tenantProvider,
    ITrialGuard trialGuard) : IRentalLayoutService
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
        await trialGuard.EnsureWritableAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(request.Name))
        {
            throw new ArgumentException("Layout name is required.");
        }

        var layout = new RentalLayout
        {
            TenantId = tenantId,
            UnitId = request.UnitId,
            Name = request.Name.Trim(),
            IsActive = request.IsActive,
            AspectRatio = FitAspectRatio(request.AspectRatio),
            WidthPercent = FitWidthPercent(request.WidthPercent),
        };

        foreach (var item in request.Items)
        {
            await EnsureRentableAsync(item.RentalAssetId, cancellationToken);
            var fitted = FitPercents(item);
            layout.AddItem(new RentalLayoutItem
            {
                TenantId = tenantId,
                LayoutId = layout.Id,
                RentalAssetId = item.RentalAssetId,
                XPercent = fitted.X,
                YPercent = fitted.Y,
                WidthPercent = fitted.W,
                HeightPercent = fitted.H,
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
        await trialGuard.EnsureWritableAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(request.Name))
        {
            throw new ArgumentException("Layout name is required.");
        }

        var layout = await dbContext.RentalLayouts
            .Include(l => l.Items)
            .FirstOrDefaultAsync(l => l.Id == id, cancellationToken)
            ?? throw new KeyNotFoundException("Layout was not found.");

        layout.UnitId = request.UnitId;
        layout.Name = request.Name.Trim();
        layout.IsActive = request.IsActive;
        layout.AspectRatio = FitAspectRatio(request.AspectRatio);
        layout.WidthPercent = FitWidthPercent(request.WidthPercent);
        layout.Touch();

        dbContext.RentalLayoutItems.RemoveRange(layout.Items);

        foreach (var item in request.Items)
        {
            await EnsureRentableAsync(item.RentalAssetId, cancellationToken);
            var fitted = FitPercents(item);
            dbContext.RentalLayoutItems.Add(new RentalLayoutItem
            {
                TenantId = tenantId,
                LayoutId = layout.Id,
                RentalAssetId = item.RentalAssetId,
                XPercent = fitted.X,
                YPercent = fitted.Y,
                WidthPercent = fitted.W,
                HeightPercent = fitted.H,
                ZIndex = item.ZIndex,
            });
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        return (await GetAsync(layout.Id, cancellationToken))!;
    }

    public async Task DeleteAsync(Guid id, CancellationToken cancellationToken)
    {
        EnsureTenant();
        await trialGuard.EnsureWritableAsync(cancellationToken);
        var layout = await dbContext.RentalLayouts
            .FirstOrDefaultAsync(l => l.Id == id, cancellationToken)
            ?? throw new KeyNotFoundException("Layout was not found.");

        dbContext.RentalLayouts.Remove(layout);
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private async Task EnsureRentableAsync(Guid rentalAssetId, CancellationToken cancellationToken)
    {
        var ok = await dbContext.RentalAssets.AnyAsync(r => r.Id == rentalAssetId && r.IsActive, cancellationToken);
        if (!ok)
        {
            throw new KeyNotFoundException($"Rentable '{rentalAssetId}' was not found.");
        }
    }

    private static (double X, double Y, double W, double H) FitPercents(
        UpsertRentalLayoutItemRequestDto item)
    {
        var width = Math.Clamp(item.WidthPercent, 8, 100);
        var height = Math.Clamp(item.HeightPercent, 8, 100);
        var x = Math.Clamp(item.XPercent, 0, 100 - width);
        var y = Math.Clamp(item.YPercent, 0, 100 - height);
        return (x, y, width, height);
    }

    private static double FitAspectRatio(double value) =>
        Math.Clamp(value <= 0 ? 1.6 : value, 0.7, 2.8);

    private static double FitWidthPercent(double value) =>
        Math.Clamp(value <= 0 ? 100 : value, 50, 100);

    private Guid EnsureTenant() =>
        tenantProvider.TenantId
        ?? throw new UnauthorizedAccessException("Tenant context is required.");

    private static RentalLayoutResponseDto ToDto(RentalLayout layout) =>
        new(
            layout.Id,
            layout.UnitId,
            layout.Name,
            layout.IsActive,
            layout.AspectRatio,
            layout.WidthPercent,
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
