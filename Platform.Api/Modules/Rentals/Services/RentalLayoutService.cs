using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Platform.Api.Modules.Rentals.Dtos;
using Platform.Api.Services.Trial;
using Platform.Core.Domain.Entities;
using Platform.Core.Infrastructure.Persistence;

namespace Platform.Api.Modules.Rentals.Services;

public sealed partial class RentalLayoutService(AppDbContext dbContext, ITenantProvider tenantProvider,
    ITrialGuard trialGuard, ILogger<RentalLayoutService>? logger = null) : IRentalLayoutService
{
    private IQueryable<RentalLayout> Layouts(Guid tenantId) => dbContext.RentalLayouts.IgnoreQueryFilters()
        .Where(l => l.TenantId == tenantId);

    public async Task<IReadOnlyList<RentalLayoutResponseDto>> ListAsync(CancellationToken ct)
    {
        var tenantId = EnsureTenant();
        var layouts = await Layouts(tenantId).AsNoTracking().OrderBy(l => l.SortOrder).ThenBy(l => l.Name).ToListAsync(ct);
        var items = await ReadItemsAsync(tenantId, layouts.Select(l => l.Id).ToArray(), false, ct);
        return layouts.Select(l => LegacyDto(l, items.Where(i => i.LayoutId == l.Id).Select(i => i.Dto))).ToList();
    }

    public async Task<RentalLayoutResponseDto?> GetAsync(Guid id, CancellationToken ct)
    {
        var tenantId = EnsureTenant();
        var layout = await Layouts(tenantId).AsNoTracking().FirstOrDefaultAsync(l => l.Id == id, ct);
        if (layout is null) return null;
        return LegacyDto(layout, (await ReadItemsAsync(tenantId, [id], false, ct)).Select(i => i.Dto));
    }

    // Existing public route retains its shape and flag behavior, with active resources only.
    public async Task<IReadOnlyList<RentalLayoutResponseDto>> ListLegacyPublicAsync(CancellationToken ct)
    {
        var tenantId = EnsureTenant();
        var layouts = await ActiveLayouts(tenantId).AsNoTracking().OrderBy(l => l.SortOrder).ThenBy(l => l.Name).ToListAsync(ct);
        var items = await ReadItemsAsync(tenantId, layouts.Select(l => l.Id).ToArray(), true, ct);
        return layouts.Select(l => LegacyDto(l, items.Where(i => i.LayoutId == l.Id).Select(i => i.Dto))).ToList();
    }

    public async Task<RentalLayoutResponseDto> CreateAsync(UpsertRentalLayoutRequestDto request, CancellationToken ct)
    {
        var tenantId = EnsureTenant();
        await trialGuard.EnsureWritableAsync(ct);
        await using var tx = await BeginWriteAsync(tenantId, ct);
        BookingMapValidation.Canvas(request.Name, request.AspectRatio, request.WidthPercent);
        await ValidateResourcesAsync(tenantId, request.UnitId, request.Items.Select(i => i.RentalAssetId).ToArray(), ct);
        foreach (var i in request.Items) BookingMapValidation.Rectangle(i.XPercent, i.YPercent, i.WidthPercent, i.HeightPercent, 0);
        var layout = new RentalLayout { TenantId = tenantId, UnitId = request.UnitId,
            Name = request.Name.Trim(), IsActive = request.IsActive, AspectRatio = request.AspectRatio,
            WidthPercent = request.WidthPercent };
        dbContext.RentalLayouts.Add(layout);
        foreach (var i in request.Items) dbContext.RentalLayoutItems.Add(new RentalLayoutItem
        { TenantId = tenantId, LayoutId = layout.Id, RentalAssetId = i.RentalAssetId,
            XPercent = i.XPercent, YPercent = i.YPercent, WidthPercent = i.WidthPercent,
            HeightPercent = i.HeightPercent, ZIndex = i.ZIndex });
        await SaveAsync(tx, ct);
        Audit("create", tenantId, layout.Id, layout.Version);
        return (await GetAsync(layout.Id, ct))!;
    }

    public async Task<RentalLayoutResponseDto> UpdateAsync(Guid id, UpsertRentalLayoutRequestDto request, CancellationToken ct)
    {
        var tenantId = EnsureTenant();
        await trialGuard.EnsureWritableAsync(ct);
        await using var tx = await BeginWriteAsync(tenantId, ct);
        var layout = await Layouts(tenantId).FirstOrDefaultAsync(l => l.Id == id, ct) ?? throw BookingMapValidation.Missing();
        BookingMapValidation.Canvas(request.Name, request.AspectRatio, request.WidthPercent);
        await ValidateResourcesAsync(tenantId, request.UnitId, request.Items.Select(i => i.RentalAssetId).ToArray(), ct);
        foreach (var i in request.Items) BookingMapValidation.Rectangle(i.XPercent, i.YPercent, i.WidthPercent, i.HeightPercent, 0);
        var existing = await dbContext.RentalLayoutItems.IgnoreQueryFilters()
            .Where(i => i.TenantId == tenantId && i.LayoutId == id).ToListAsync(ct);
        foreach (var old in existing.Where(i => !request.Items.Any(n => n.RentalAssetId == i.RentalAssetId)))
            dbContext.RentalLayoutItems.Remove(old);
        foreach (var i in request.Items)
        {
            var item = existing.SingleOrDefault(e => e.RentalAssetId == i.RentalAssetId);
            if (item is null)
            {
                item = new RentalLayoutItem { TenantId = tenantId, LayoutId = id, RentalAssetId = i.RentalAssetId,
                    XPercent = i.XPercent, YPercent = i.YPercent, WidthPercent = i.WidthPercent,
                    HeightPercent = i.HeightPercent, ZIndex = i.ZIndex };
                dbContext.RentalLayoutItems.Add(item);
            }
            else
            {
                item.XPercent = i.XPercent; item.YPercent = i.YPercent;
                item.WidthPercent = i.WidthPercent; item.HeightPercent = i.HeightPercent; item.ZIndex = i.ZIndex;
                item.Touch(); // Rotation and LockAspectRatio deliberately preserved by legacy writes.
            }
        }
        layout.UnitId = request.UnitId; layout.Name = request.Name.Trim(); layout.IsActive = request.IsActive;
        layout.AspectRatio = request.AspectRatio; layout.WidthPercent = request.WidthPercent;
        layout.Version++; layout.Touch();
        await SaveAsync(tx, ct);
        Audit("legacy-update", tenantId, id, layout.Version);
        return (await GetAsync(id, ct))!;
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct)
    {
        var tenantId = EnsureTenant();
        await trialGuard.EnsureWritableAsync(ct);
        await using var tx = await BeginWriteAsync(tenantId, ct);
        var layout = await Layouts(tenantId).FirstOrDefaultAsync(l => l.Id == id, ct) ?? throw BookingMapValidation.Missing();
        var settings = await dbContext.Set<BookingMapSettings>().IgnoreQueryFilters()
            .SingleOrDefaultAsync(s => s.TenantId == tenantId && s.DefaultLayoutId == id, ct);
        if (settings is not null) { settings.DefaultLayoutId = null; settings.Version++; settings.UpdatedAt = DateTimeOffset.UtcNow; }
        dbContext.RentalLayouts.Remove(layout);
        await SaveAsync(tx, ct);
        Audit("delete", tenantId, id, layout.Version);
    }

    private async Task<IDbContextTransaction?> BeginWriteAsync(Guid tenantId, CancellationToken ct)
    {
        var tx = dbContext.Database.IsRelational() && dbContext.Database.CurrentTransaction is null
            ? await dbContext.Database.BeginTransactionAsync(ct) : null;
        try { await BookingMapLocks.AcquireAsync(dbContext, tenantId, ct); return tx; }
        catch { if (tx is not null) await tx.DisposeAsync(); throw; }
    }
    private async Task SaveAsync(IDbContextTransaction? tx, CancellationToken ct)
    {
        try { await dbContext.SaveChangesAsync(ct); if (tx is not null) await tx.CommitAsync(ct); }
        catch (DbUpdateConcurrencyException) { throw BookingMapValidation.Conflict(); }
    }
    private async Task ValidateResourcesAsync(Guid tenantId, Guid? unitId, IReadOnlyCollection<Guid> ids, CancellationToken ct)
    {
        if (ids.Count > 1000 || ids.Any(i => i == Guid.Empty) || ids.Distinct().Count() != ids.Count)
            throw BookingMapValidation.Invalid("Map resources must be unique, non-empty and limited to 1000.");
        if (unitId is { } unit && !await dbContext.Units.IgnoreQueryFilters().AnyAsync(u => u.TenantId == tenantId && u.Id == unit && u.IsActive, ct))
            throw BookingMapValidation.Invalid("Unit was not found.");
        var valid = await (from r in dbContext.RentalAssets.IgnoreQueryFilters()
            join a in dbContext.Assets.IgnoreQueryFilters() on r.AssetId equals a.Id
            where r.TenantId == tenantId && a.TenantId == tenantId && r.IsActive && a.IsRentable
                && a.Status != Platform.Core.Domain.Enums.AssetStatus.Inactive
                && ids.Contains(r.Id) && (unitId == null || a.UnitId == unitId)
            select r.Id).CountAsync(ct);
        if (valid != ids.Count) throw BookingMapValidation.Invalid("Every resource must be active and belong to this tenant and map unit.");
    }
    private Guid EnsureTenant() => tenantProvider.TenantId is { } id && id != Guid.Empty
        ? id : throw new UnauthorizedAccessException("Tenant context is required.");
    private void Audit(string operation, Guid tenantId, Guid id, long version) =>
        logger?.LogInformation("BookingMap {Operation} TenantId={TenantId} LayoutId={LayoutId} Version={Version}", operation, tenantId, id, version);
    private static RentalLayoutResponseDto LegacyDto(RentalLayout l, IEnumerable<BookingMapItemDto> items) =>
        new(l.Id, l.UnitId, l.Name, l.IsActive, l.AspectRatio, l.WidthPercent, items.Select(i => new RentalLayoutItemResponseDto(
            i.Id, i.RentalAssetId, i.AssetName, i.XPercent, i.YPercent, i.WidthPercent, i.HeightPercent, i.ZIndex)).ToList());
}
