using Microsoft.EntityFrameworkCore;
using Platform.Api.Modules.Rentals.Dtos;
using Platform.Core.Domain.Entities;
using Platform.Core.Infrastructure.Persistence;

namespace Platform.Api.Modules.Rentals.Services;

public sealed class OccupancyKindService(
    AppDbContext dbContext,
    ITenantProvider tenantProvider) : IOccupancyKindService
{
    private static readonly (string Key, string Label, bool Bookable, bool Blocks, string Color, int Order)[] Defaults =
    [
        ("open", "Open", true, false, "#22C55E", 0),
        ("closed", "Closed", false, true, "#94A3B8", 1),
        ("lesson", "Lesson", false, true, "#3B82F6", 2),
    ];

    public async Task<IReadOnlyList<OccupancyKindResponseDto>> ListAsync(
        CancellationToken cancellationToken)
    {
        await EnsureDefaultsAsync(cancellationToken);

        var items = await dbContext.OccupancyKinds
            .AsNoTracking()
            .OrderBy(k => k.SortOrder)
            .ThenBy(k => k.Label)
            .ToListAsync(cancellationToken);

        return items.Select(ToDto).ToList();
    }

    public async Task<OccupancyKindResponseDto> CreateAsync(
        UpsertOccupancyKindRequestDto request,
        CancellationToken cancellationToken)
    {
        var tenantId = EnsureTenant();
        var key = NormalizeKey(request.Key);

        var exists = await dbContext.OccupancyKinds
            .AnyAsync(k => k.Key == key, cancellationToken);
        if (exists)
        {
            throw new ArgumentException($"Occupancy kind '{key}' already exists.");
        }

        var entity = new OccupancyKind
        {
            TenantId = tenantId,
            Key = key,
            Label = request.Label.Trim(),
            ColorHex = NormalizeColor(request.ColorHex),
            IsBookableByCustomer = request.IsBookableByCustomer,
            BlocksCapacity = request.BlocksCapacity,
            SortOrder = request.SortOrder,
            IsActive = request.IsActive,
        };

        dbContext.OccupancyKinds.Add(entity);
        await dbContext.SaveChangesAsync(cancellationToken);
        return ToDto(entity);
    }

    public async Task<OccupancyKindResponseDto> UpdateAsync(
        Guid id,
        UpsertOccupancyKindRequestDto request,
        CancellationToken cancellationToken)
    {
        EnsureTenant();
        var entity = await dbContext.OccupancyKinds
            .FirstOrDefaultAsync(k => k.Id == id, cancellationToken)
            ?? throw new KeyNotFoundException("Occupancy kind was not found.");

        var key = NormalizeKey(request.Key);
        var clash = await dbContext.OccupancyKinds
            .AnyAsync(k => k.Key == key && k.Id != id, cancellationToken);
        if (clash)
        {
            throw new ArgumentException($"Occupancy kind '{key}' already exists.");
        }

        entity.Key = key;
        entity.Label = request.Label.Trim();
        entity.ColorHex = NormalizeColor(request.ColorHex);
        entity.IsBookableByCustomer = request.IsBookableByCustomer;
        entity.BlocksCapacity = request.BlocksCapacity;
        entity.SortOrder = request.SortOrder;
        entity.IsActive = request.IsActive;
        entity.Touch();

        await dbContext.SaveChangesAsync(cancellationToken);
        return ToDto(entity);
    }

    public async Task EnsureDefaultsAsync(CancellationToken cancellationToken)
    {
        var tenantId = EnsureTenant();
        var any = await dbContext.OccupancyKinds.AnyAsync(cancellationToken);
        if (any)
        {
            return;
        }

        foreach (var item in Defaults)
        {
            dbContext.OccupancyKinds.Add(new OccupancyKind
            {
                TenantId = tenantId,
                Key = item.Key,
                Label = item.Label,
                ColorHex = item.Color,
                IsBookableByCustomer = item.Bookable,
                BlocksCapacity = item.Blocks,
                SortOrder = item.Order,
                IsActive = true,
            });
        }

        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private Guid EnsureTenant() =>
        tenantProvider.TenantId
        ?? throw new UnauthorizedAccessException("Tenant context is required.");

    private static string NormalizeKey(string key)
    {
        var trimmed = key.Trim().ToLowerInvariant();
        if (trimmed.Length is < 2 or > 64)
        {
            throw new ArgumentException("Occupancy kind key must be 2–64 characters.");
        }

        return trimmed;
    }

    private static string? NormalizeColor(string? color)
    {
        if (string.IsNullOrWhiteSpace(color))
        {
            return null;
        }

        var value = color.Trim();
        if (!value.StartsWith('#'))
        {
            value = "#" + value;
        }

        return value.Length is 7 or 4 ? value : throw new ArgumentException("Invalid color hex.");
    }

    private static OccupancyKindResponseDto ToDto(OccupancyKind k) =>
        new(k.Id, k.Key, k.Label, k.ColorHex, k.IsBookableByCustomer, k.BlocksCapacity, k.SortOrder, k.IsActive);
}
