using System.Data;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Platform.Api.Modules.Rentals.Dtos;
using Platform.Core.Domain.Entities;

namespace Platform.Api.Modules.Rentals.Services;

public sealed partial class RentalLayoutService
{
    public async Task<BookingMapCompositionDto?> GetCompositionAsync(Guid id, CancellationToken ct)
    {
        var tenantId = EnsureTenant();
        await using var snapshot = await BeginCompositionReadAsync(ct);
        var map = await Layouts(tenantId).AsNoTracking().FirstOrDefaultAsync(l => l.Id == id, ct);
        if (map is null) return null;
        var items = await ReadItemsAsync(tenantId, [id], false, ct);
        return new(map.Id, map.UnitId, map.Name, map.IsActive, map.SortOrder, map.AspectRatio,
            map.WidthPercent, map.Version, items.Select(i => i.Dto).ToList(), await ReadDecorationsAsync(tenantId, id, ct));
    }

    public async Task<BookingMapCompositionDto> SaveCompositionAsync(Guid id, SaveBookingMapCompositionRequest request, CancellationToken ct)
    {
        var tenantId = EnsureTenant();
        await trialGuard.EnsureWritableAsync(ct);
        await using var tx = await BeginWriteAsync(tenantId, ct);
        var map = await Layouts(tenantId).FirstOrDefaultAsync(l => l.Id == id, ct) ?? throw BookingMapValidation.Missing();
        if (map.Version != request.ExpectedVersion) throw BookingMapValidation.Conflict();
        BookingMapValidation.Canvas(request.Name, request.AspectRatio, request.WidthPercent);
        if (request.Items is null || request.Decorations is null || request.Decorations.Count > 500)
            throw BookingMapValidation.Invalid("Invalid composition size.");
        await ValidateResourcesAsync(tenantId, request.UnitId, request.Items.Select(i => i.RentalAssetId).ToArray(), ct);
        foreach (var i in request.Items) BookingMapValidation.Rectangle(i.XPercent, i.YPercent, i.WidthPercent, i.HeightPercent, i.Rotation);
        var validatedDecorations = request.Decorations.Select(BookingMapValidation.Decoration).ToArray();
        var items = await dbContext.RentalLayoutItems.IgnoreQueryFilters().Where(i => i.TenantId == tenantId && i.LayoutId == id).ToListAsync(ct);
        var decorations = await dbContext.Set<LayoutDecoration>().IgnoreQueryFilters().Where(d => d.TenantId == tenantId && d.LayoutId == id).ToListAsync(ct);
        ValidateIds(request.Items.Select(i => i.Id), items.Select(i => i.Id));
        ValidateIds(request.Decorations.Select(i => i.Id), decorations.Select(i => i.Id));
        foreach (var i in request.Items.Where(i => i.Id is not null))
            if (items.Single(e => e.Id == i.Id).RentalAssetId != i.RentalAssetId)
                throw BookingMapValidation.Invalid("An existing item cannot be assigned to a different resource.");
        // A resource keeps its identity even when a client omits its existing item id.
        foreach (var old in items.Where(e => !request.Items.Any(i => i.RentalAssetId == e.RentalAssetId)))
            dbContext.RentalLayoutItems.Remove(old);
        foreach (var i in request.Items)
        {
            var item = items.SingleOrDefault(e => e.RentalAssetId == i.RentalAssetId);
            if (item is null)
            {
                item = new RentalLayoutItem { TenantId = tenantId, LayoutId = id, RentalAssetId = i.RentalAssetId,
                    XPercent = i.XPercent, YPercent = i.YPercent, WidthPercent = i.WidthPercent, HeightPercent = i.HeightPercent, ZIndex = i.ZIndex };
                dbContext.RentalLayoutItems.Add(item);
            }
            item.XPercent = i.XPercent; item.YPercent = i.YPercent; item.WidthPercent = i.WidthPercent;
            item.HeightPercent = i.HeightPercent; item.ZIndex = i.ZIndex;
            item.Rotation = i.Rotation; item.LockAspectRatio = i.LockAspectRatio; item.Touch();
        }
        foreach (var old in decorations.Where(e => !request.Decorations.Any(i => i.Id == e.Id)))
            dbContext.Set<LayoutDecoration>().Remove(old);
        for (var n = 0; n < request.Decorations.Count; n++)
        {
            var d = request.Decorations[n];
            var entity = d.Id is { } decorationId ? decorations.Single(e => e.Id == decorationId) : new LayoutDecoration { TenantId = tenantId, LayoutId = id };
            if (d.Id is null) dbContext.Set<LayoutDecoration>().Add(entity);
            entity.Kind = d.Kind; entity.Label = d.Label; entity.IconKey = d.IconKey;
            entity.XPercent = d.XPercent; entity.YPercent = d.YPercent; entity.WidthPercent = d.WidthPercent;
            entity.HeightPercent = d.HeightPercent; entity.Rotation = d.Rotation; entity.ZIndex = d.ZIndex;
            entity.StyleJson = validatedDecorations[n].Style; entity.GeometryJson = validatedDecorations[n].Geometry; entity.Touch();
        }
        map.Name = request.Name.Trim(); map.UnitId = request.UnitId; map.IsActive = request.IsActive;
        map.SortOrder = request.SortOrder; map.AspectRatio = request.AspectRatio; map.WidthPercent = request.WidthPercent;
        map.Version++; map.Touch();
        await SaveAsync(tx, ct);
        Audit("composition", tenantId, id, map.Version);
        // A committed write transaction must be detached before opening the read snapshot.
        if (tx is not null) await tx.DisposeAsync();
        return (await GetCompositionAsync(id, ct))!;
    }

    public async Task<BookingMapSettingsDto> GetSettingsAsync(CancellationToken ct)
    {
        var tenantId = EnsureTenant();
        var settings = await dbContext.Set<BookingMapSettings>().IgnoreQueryFilters().AsNoTracking().SingleOrDefaultAsync(s => s.TenantId == tenantId, ct);
        if (settings is not null) return SettingsDto(settings);
        var tenant = await dbContext.Tenants.AsNoTracking().SingleOrDefaultAsync(t => t.Id == tenantId, ct)
            ?? throw BookingMapValidation.Missing();
        var fallback = new BookingMapSettings { TenantId = tenantId, Version = 0 };
        if (BookingMapValidation.IsColor(tenant.PrimaryColor)) fallback.SelectedColor = tenant.PrimaryColor!;
        if (BookingMapValidation.IsColor(tenant.AccentColor)) fallback.AvailableColor = tenant.AccentColor!;
        return SettingsDto(fallback);
    }

    public async Task<BookingMapSettingsDto> SaveSettingsAsync(SaveBookingMapSettingsRequest request, CancellationToken ct)
    {
        var tenantId = EnsureTenant();
        await trialGuard.EnsureWritableAsync(ct);
        await using var tx = await BeginWriteAsync(tenantId, ct);
        if (!BookingMapValidation.IsColor(request.AvailableColor) || !BookingMapValidation.IsColor(request.TakenColor)
            || !BookingMapValidation.IsColor(request.UnavailableColor) || !BookingMapValidation.IsColor(request.SelectedColor)
            || request.TimeFilterMode is not ("Periods" or "FromTime") || request.DefaultView is not ("Map" or "List"))
            throw BookingMapValidation.Invalid("Invalid state colors, temporal filter or default view.");
        if (request.DefaultLayoutId is { } id && !await Layouts(tenantId).AnyAsync(l => l.Id == id, ct))
            throw BookingMapValidation.Invalid("Default map was not found.");
        var settings = await dbContext.Set<BookingMapSettings>().IgnoreQueryFilters().SingleOrDefaultAsync(s => s.TenantId == tenantId, ct);
        if ((settings?.Version ?? 0) != request.ExpectedVersion) throw BookingMapValidation.Conflict();
        if (settings is null) { settings = new BookingMapSettings { TenantId = tenantId, Version = 0 }; dbContext.Add(settings); }
        settings.AvailableColor = request.AvailableColor.ToUpperInvariant(); settings.TakenColor = request.TakenColor.ToUpperInvariant();
        settings.UnavailableColor = request.UnavailableColor.ToUpperInvariant(); settings.SelectedColor = request.SelectedColor.ToUpperInvariant();
        settings.ShowPriceOnBlock = request.ShowPriceOnBlock; settings.TimeFilterMode = request.TimeFilterMode;
        settings.DefaultView = request.DefaultView; settings.DefaultLayoutId = request.DefaultLayoutId;
        settings.Version++; settings.UpdatedAt = DateTimeOffset.UtcNow;
        await SaveAsync(tx, ct);
        Audit("settings", tenantId, settings.DefaultLayoutId ?? Guid.Empty, settings.Version);
        return SettingsDto(settings);
    }

    public async Task<PublicBookingMapListDto> ListPublicMapsAsync(Guid? unitId, CancellationToken ct)
    {
        var tenantId = EnsureTenant();
        var maps = await ActiveLayouts(tenantId).AsNoTracking().Where(l => unitId == null || l.UnitId == unitId)
            .OrderBy(l => l.SortOrder).ThenBy(l => l.Name).ThenBy(l => l.Id)
            .Select(l => new PublicBookingMapSummaryDto(l.Id, l.UnitId, l.Name, l.AspectRatio, l.WidthPercent, l.SortOrder, l.Version)).ToListAsync(ct);
        var settings = await GetSettingsAsync(ct);
        var preferred = maps.Any(m => m.Id == settings.DefaultLayoutId) ? settings.DefaultLayoutId : maps.FirstOrDefault()?.Id;
        settings = settings with { DefaultLayoutId = preferred, DefaultView = maps.Count == 0 ? "List" : settings.DefaultView };
        return new(maps, preferred, settings);
    }

    public async Task<PublicBookingMapDto?> GetPublicMapAsync(Guid id, Guid? unitId, CancellationToken ct)
    {
        var tenantId = EnsureTenant();
        await using var snapshot = await BeginCompositionReadAsync(ct);
        var map = await ActiveLayouts(tenantId).AsNoTracking().FirstOrDefaultAsync(l => l.Id == id && (unitId == null || l.UnitId == unitId), ct);
        if (map is null) return null;
        var items = (await ReadItemsAsync(tenantId, [id], true, ct)).Select(i => i.Dto).ToList();
        var revisionIds = items.Select(i => i.VisualTemplateRevisionId).Where(i => i != null).Select(i => i!.Value).Distinct().ToArray();
        // Referenced published revisions remain renderable even after the template is archived.
        var visuals = await dbContext.Set<ResourceVisualTemplateRevision>().AsNoTracking()
            .Where(r => revisionIds.Contains(r.Id) && r.Status == ResourceVisualRevisionStatus.Published)
            .OrderBy(r => r.Id).Select(r => new PublicVisualRevisionDto(r.Id, r.TemplateId, r.RevisionNo,
                r.FloorSvg, r.LinesSvg, r.NetSvg, r.ViewBox, r.AspectRatio, r.DefaultColor, r.DefaultIcon, r.ArtSha256)).ToListAsync(ct);
        var publishedIds = visuals.Select(r => r.Id).ToHashSet();
        items = items.Select(i => i.VisualTemplateRevisionId is { } revisionId && !publishedIds.Contains(revisionId)
            ? i with { VisualTemplateRevisionId = null } : i).ToList();
        return new(map.Id, map.UnitId, map.Name, map.AspectRatio, map.WidthPercent, map.Version,
            items, await ReadDecorationsAsync(tenantId, id, ct), visuals);
    }

    public async Task<BookingMapCompositionDto> SeedDemoAsync(SeedDemoBookingMapRequest request, CancellationToken ct)
    {
        var tenantId = EnsureTenant();
        await trialGuard.EnsureWritableAsync(ct);
        await using var tx = await BeginWriteAsync(tenantId, ct);
        if (!await dbContext.Tenants.AnyAsync(t => t.Id == tenantId && t.IsActive, ct)) throw BookingMapValidation.Missing();
        BookingMapValidation.Canvas(request.Name, request.AspectRatio, 100);
        if (request.Items is null || request.Items.Count == 0 || request.Decorations is null || request.Decorations.Count > 500)
            throw BookingMapValidation.Invalid("Provide an explicit non-empty positioning manifest.");
        await ValidateResourcesAsync(tenantId, request.UnitId, request.Items.Select(i => i.RentalAssetId).ToArray(), ct);
        foreach (var i in request.Items)
        {
            BookingMapValidation.Rectangle(i.XPercent, i.YPercent, i.WidthPercent, i.HeightPercent, i.Rotation);
            if (i.Id is not null) throw BookingMapValidation.Invalid("New demo placements do not accept existing item ids.");
        }
        foreach (var d in request.Decorations)
        {
            BookingMapValidation.Decoration(d);
            if (d.Id is not null) throw BookingMapValidation.Invalid("New demo decorations do not accept existing ids.");
        }
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes("booking-map-demo-v1:" + tenantId.ToString("D")));
        var id = new Guid(hash.AsSpan(0, 16));
        if (await Layouts(tenantId).AnyAsync(l => l.Id == id, ct))
        {
            if (tx is not null) await tx.DisposeAsync();
            return (await GetCompositionAsync(id, ct))!;
        }
        var map = new RentalLayout(id) { TenantId = tenantId, UnitId = request.UnitId, Name = request.Name.Trim(),
            IsActive = true, AspectRatio = request.AspectRatio, WidthPercent = 100 };
        dbContext.Add(map);
        foreach (var i in request.Items) dbContext.Add(new RentalLayoutItem { TenantId = tenantId, LayoutId = id,
            RentalAssetId = i.RentalAssetId, XPercent = i.XPercent, YPercent = i.YPercent, WidthPercent = i.WidthPercent,
            HeightPercent = i.HeightPercent, Rotation = i.Rotation, LockAspectRatio = i.LockAspectRatio, ZIndex = i.ZIndex });
        foreach (var d in request.Decorations)
        {
            var json = BookingMapValidation.Decoration(d);
            dbContext.Add(new LayoutDecoration { TenantId = tenantId, LayoutId = id, Kind = d.Kind, Label = d.Label,
                IconKey = d.IconKey, XPercent = d.XPercent, YPercent = d.YPercent, WidthPercent = d.WidthPercent,
                HeightPercent = d.HeightPercent, Rotation = d.Rotation, ZIndex = d.ZIndex, StyleJson = json.Style, GeometryJson = json.Geometry });
        }
        await SaveAsync(tx, ct);
        Audit("demo-seed", tenantId, id, map.Version);
        if (tx is not null) await tx.DisposeAsync();
        return (await GetCompositionAsync(id, ct))!;
    }

    // PostgreSQL's default ReadCommitted takes a snapshot per statement. Keep metadata,
    // placements, decorations and referenced art on the same snapshot until materialized.
    private async Task<IDbContextTransaction?> BeginCompositionReadAsync(CancellationToken ct) =>
        dbContext.Database.IsRelational()
            ? await dbContext.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct)
            : null;

    private IQueryable<RentalLayout> ActiveLayouts(Guid tenantId) => Layouts(tenantId).Where(l => l.IsActive
        && (l.UnitId == null || dbContext.Units.IgnoreQueryFilters().Any(u => u.TenantId == tenantId && u.Id == l.UnitId && u.IsActive)));
    private sealed record ItemRow(Guid LayoutId, BookingMapItemDto Dto);
    private async Task<List<ItemRow>> ReadItemsAsync(Guid tenantId, Guid[] layoutIds, bool activeOnly, CancellationToken ct) =>
        await (from i in dbContext.RentalLayoutItems.IgnoreQueryFilters().AsNoTracking()
            join r in dbContext.RentalAssets.IgnoreQueryFilters() on i.RentalAssetId equals r.Id
            join a in dbContext.Assets.IgnoreQueryFilters() on r.AssetId equals a.Id
            where i.TenantId == tenantId && r.TenantId == tenantId && a.TenantId == tenantId
                && layoutIds.Contains(i.LayoutId) && (!activeOnly || (r.IsActive && a.IsRentable
                    && a.Status != Platform.Core.Domain.Enums.AssetStatus.Inactive
                    && dbContext.Units.IgnoreQueryFilters().Any(u => u.TenantId == tenantId && u.Id == a.UnitId && u.IsActive)))
            orderby i.ZIndex, i.Id
            select new ItemRow(i.LayoutId, new BookingMapItemDto(i.Id, r.Id, a.Name, i.XPercent, i.YPercent,
                i.WidthPercent, i.HeightPercent, i.ZIndex, i.Rotation, i.LockAspectRatio, r.VisualTemplateRevisionId, r.CapacityPeople))).ToListAsync(ct);
    private async Task<IReadOnlyList<BookingMapDecorationDto>> ReadDecorationsAsync(Guid tenantId, Guid layoutId, CancellationToken ct)
    {
        var rows = await dbContext.Set<LayoutDecoration>().IgnoreQueryFilters().AsNoTracking()
            .Where(d => d.TenantId == tenantId && d.LayoutId == layoutId).OrderBy(d => d.ZIndex).ThenBy(d => d.Id).ToListAsync(ct);
        return rows.Select(d => new BookingMapDecorationDto(d.Id, d.Kind, d.Label, d.IconKey, d.XPercent, d.YPercent,
            d.WidthPercent, d.HeightPercent, d.Rotation, d.ZIndex, JsonSerializer.Deserialize<JsonElement>(d.StyleJson),
            d.GeometryJson is null ? null : JsonSerializer.Deserialize<JsonElement>(d.GeometryJson))).ToList();
    }
    private static void ValidateIds(IEnumerable<Guid?> requested, IEnumerable<Guid> existing)
    {
        var ids = requested.Where(i => i is not null).Select(i => i!.Value).ToArray();
        var owned = existing.ToHashSet();
        if (ids.Distinct().Count() != ids.Length || ids.Any(i => i == Guid.Empty || !owned.Contains(i)))
            throw BookingMapValidation.Invalid("Placement ids must be unique and belong to this map.");
    }
    private static BookingMapSettingsDto SettingsDto(BookingMapSettings s) => new(s.AvailableColor, s.TakenColor,
        s.UnavailableColor, s.SelectedColor, s.ShowPriceOnBlock, s.TimeFilterMode, s.DefaultView, s.DefaultLayoutId, s.Version);
}
