using Microsoft.EntityFrameworkCore;
using Platform.Api.Modules.Assets.Dtos;
using Platform.Api.Services.Trial;
using Platform.Api.Modules.Rentals.Services;
using Platform.Api.Modules.ResourceVisuals;
using Platform.Api.Serialization;
using Platform.Core.Domain.Entities;
using Platform.Core.Domain.Enums;
using Platform.Core.Infrastructure.Persistence;

namespace Platform.Api.Modules.Assets.Services;

public sealed class AssetService(
    AppDbContext dbContext,
    ITenantProvider tenantProvider,
    ITrialGuard trialGuard,
    ILogger<AssetService>? logger = null) : IAssetService
{
    private const int MaxBulkCreateCount = 1000;

    public async Task<IReadOnlyList<AssetResponse>> ListAsync(
        CancellationToken cancellationToken)
    {
        var tenantId = EnsureTenantContext();

        var assets = await dbContext.Assets
            .AsNoTracking()
            .Include(a => a.RentalConfiguration)
            .Where(a => a.TenantId == tenantId)
            .OrderBy(a => a.Tag)
            .ToListAsync(cancellationToken);

        return assets.Select(ToResponse).ToList();
    }

    public async Task<AssetResponse?> GetByIdAsync(
        Guid id,
        CancellationToken cancellationToken)
    {
        var tenantId = EnsureTenantContext();

        var asset = await dbContext.Assets
            .AsNoTracking()
            .Include(a => a.RentalConfiguration)
            .FirstOrDefaultAsync(a => a.Id == id && a.TenantId == tenantId, cancellationToken);

        return asset is null ? null : ToResponse(asset);
    }

    public async Task<AssetResponse> CreateAsync(
        CreateAssetRequest request,
        CancellationToken cancellationToken)
    {
        var tenantId = EnsureTenantContext();
        await trialGuard.EnsureCanCreateAssetsAsync(1, cancellationToken);
        await using var transaction = dbContext.Database.IsRelational() && dbContext.Database.CurrentTransaction is null
            ? await dbContext.Database.BeginTransactionAsync(cancellationToken) : null;
        var visual = await ValidateVisualAssignmentAsync(request.IsRentable,
            JsonOptional<Guid>.Specified(request.VisualTemplateRevisionId), null, cancellationToken);
        ValidateCapacity(request.CapacityPeople);
        BookingPolicyText.NormalizeAdvanceDays(request.MaxAdvanceBookingDays);

        ValidateRentalFields(request.IsRentable, request.RentalType, request.TotalQuantity);
        ValidateQueueFields(request.IsRentable, request.RentalType, request.QueueEnabled, request.QueueOpeningTime);
        await EnsureUnitExistsAsync(request.UnitId, cancellationToken);
        await EnsureCategoryExistsAsync(request.CategoryId, cancellationToken);
        var family = await EnsureFamilyEnabledForTenantAsync(
            tenantId,
            request.FamilyId,
            cancellationToken);
        var attributes = AssetFamilyAttributeValidator.ValidateAndProject(
            family.FieldSchemaJson,
            request.Attributes);
        var tag = request.Tag.Trim();
        await EnsureTagAvailableAsync(tag, excludeAssetId: null, cancellationToken);

        var asset = new Asset
        {
            TenantId = tenantId,
            UnitId = request.UnitId,
            CategoryId = request.CategoryId,
            FamilyId = family.Id,
            Name = request.Name.Trim(),
            Tag = tag,
            Location = NormalizeOptional(request.Location),
            SerialNumber = NormalizeOptional(request.SerialNumber),
            InstallationDate = request.InstallationDate,
            Status = request.Status,
            IsRentable = request.IsRentable,
            RequiresMaintenance = request.RequiresMaintenance,
            Attributes = attributes,
        };

        dbContext.Assets.Add(asset);
        SyncRentalConfiguration(
            asset,
            request.IsRentable,
            request.RentalType,
            request.TotalQuantity,
            request.RequiresDeposit,
            request.QueueEnabled,
            request.QueueOpeningTime);
        ApplyVisualConfiguration(asset, JsonOptional<Guid>.Specified(request.VisualTemplateRevisionId),
            request.CapacityPeople is null ? default : JsonOptional<int>.Specified(request.CapacityPeople), request.ConfirmVisualSuggestions,
            JsonOptional<int>.Specified(request.MaxAdvanceBookingDays), visual);

        await SaveChangesProtectingTagAsync(cancellationToken);
        if (transaction is not null) await transaction.CommitAsync(cancellationToken);
        AuditVisualAssignment(asset);

        return ToResponse(asset);
    }

    public async Task<AssetResponse?> UpdateAsync(
        Guid id,
        UpdateAssetRequest request,
        CancellationToken cancellationToken)
    {
        var tenantId = EnsureTenantContext();
        await trialGuard.EnsureWritableAsync(cancellationToken);
        await using var transaction = dbContext.Database.IsRelational() && dbContext.Database.CurrentTransaction is null
            ? await dbContext.Database.BeginTransactionAsync(cancellationToken) : null;
        await BookingMapLocks.AcquireAsync(dbContext, tenantId, cancellationToken);

        ValidateRentalFields(request.IsRentable, request.RentalType, request.TotalQuantity);
        ValidateQueueFields(request.IsRentable, request.RentalType, request.QueueEnabled, request.QueueOpeningTime);

        var asset = await dbContext.Assets
            .Include(a => a.RentalConfiguration)
            .FirstOrDefaultAsync(a => a.Id == id && a.TenantId == tenantId, cancellationToken);

        if (asset is null)
        {
            return null;
        }

        var visual = await ValidateVisualAssignmentAsync(request.IsRentable,
            request.VisualTemplateRevisionId, asset.RentalConfiguration?.VisualTemplateRevisionId, cancellationToken);
        if (request.CapacityPeople.IsSpecified) ValidateCapacity(request.CapacityPeople.Value);
        if (request.MaxAdvanceBookingDays.IsSpecified) BookingPolicyText.NormalizeAdvanceDays(request.MaxAdvanceBookingDays.Value);
        if (asset.UnitId != request.UnitId && asset.RentalConfiguration is { } configuration)
        {
            var conflicts = await dbContext.RentalLayoutItems.AsNoTracking().AnyAsync(item =>
                item.TenantId == tenantId && item.RentalAssetId == configuration.Id
                && item.Layout.TenantId == tenantId && item.Layout.UnitId != null
                && item.Layout.UnitId != request.UnitId, cancellationToken);
            if (conflicts) throw new ResourceVisualException("MAP_INVALID", "The resource belongs to a map scoped to its current unit.");
        }

        await EnsureUnitExistsAsync(request.UnitId, cancellationToken);
        await EnsureCategoryExistsAsync(request.CategoryId, cancellationToken);
        var family = await EnsureFamilyEnabledForTenantAsync(
            tenantId,
            request.FamilyId,
            cancellationToken);
        var attributes = AssetFamilyAttributeValidator.ValidateAndProject(
            family.FieldSchemaJson,
            request.Attributes);
        var tag = request.Tag.Trim();
        await EnsureTagAvailableAsync(tag, asset.Id, cancellationToken);

        asset.UnitId = request.UnitId;
        asset.CategoryId = request.CategoryId;
        asset.FamilyId = family.Id;
        asset.Name = request.Name.Trim();
        asset.Tag = tag;
        asset.Location = NormalizeOptional(request.Location);
        asset.SerialNumber = NormalizeOptional(request.SerialNumber);
        asset.InstallationDate = request.InstallationDate;
        asset.Status = request.Status;
        asset.IsRentable = request.IsRentable;
        asset.RequiresMaintenance = request.RequiresMaintenance;
        asset.Attributes = attributes;
        asset.Touch();

        SyncRentalConfiguration(
            asset,
            request.IsRentable,
            request.RentalType,
            request.TotalQuantity,
            request.RequiresDeposit,
            request.QueueEnabled,
            request.QueueOpeningTime);
        ApplyVisualConfiguration(asset, request.VisualTemplateRevisionId, request.CapacityPeople,
            request.ConfirmVisualSuggestions, request.MaxAdvanceBookingDays, visual);

        await SaveChangesProtectingTagAsync(cancellationToken);
        if (transaction is not null) await transaction.CommitAsync(cancellationToken);
        AuditVisualAssignment(asset);

        return ToResponse(asset);
    }

    public async Task<DeleteAssetResult?> DeleteAsync(Guid id, CancellationToken cancellationToken)
    {
        var tenantId = EnsureTenantContext();
        await trialGuard.EnsureWritableAsync(cancellationToken);

        var asset = await dbContext.Assets
            .Include(a => a.RentalConfiguration)
            .FirstOrDefaultAsync(a => a.Id == id && a.TenantId == tenantId, cancellationToken);

        if (asset is null)
        {
            return null;
        }

        if (asset.ScheduledDeletionAt is null)
        {
            asset.ScheduledDeletionAt = DateTimeOffset.UtcNow.AddDays(30);
            asset.Touch();
            await dbContext.SaveChangesAsync(cancellationToken);

            return new DeleteAssetResult(
                PermanentlyDeleted: false,
                Asset: ToResponse(asset));
        }

        if (await dbContext.WorkOrders.AnyAsync(workOrder => workOrder.TenantId == tenantId && workOrder.AssetId == id, cancellationToken))
        {
            throw new AssetInUseException();
        }

        dbContext.Assets.Remove(asset);
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (AssetPersistenceConflicts.IsForeignKeyViolation(ex))
        {
            dbContext.ChangeTracker.Clear();
            var referencedByWorkOrders = await dbContext.WorkOrders
                .AnyAsync(workOrder => workOrder.TenantId == tenantId && workOrder.AssetId == id, cancellationToken);
            if (referencedByWorkOrders || AssetPersistenceConflicts.IsWorkOrderAssetForeignKey(ex))
            {
                throw new AssetInUseException();
            }

            throw;
        }

        return new DeleteAssetResult(
            PermanentlyDeleted: true,
            Asset: null);
    }

    public async Task<BulkCreateAssetsResponse> BulkCreateAsync(
        BulkCreateAssetsRequest request,
        CancellationToken cancellationToken)
    {
        var tenantId = EnsureTenantContext();
        var plan = ResolveBulkCreatePlan(request);
        ValidateRentalFields(request.IsRentable, plan.RentalType, plan.TotalQuantity);
        ValidateQueueFields(
            request.IsRentable,
            plan.RentalType,
            request.QueueEnabled,
            request.QueueOpeningTime);

        await trialGuard.EnsureCanCreateAssetsAsync(plan.Tags.Count, cancellationToken);

        await EnsureUnitExistsAsync(request.UnitId, cancellationToken);
        var category = await EnsureCategoryExistsAsync(request.CategoryId, cancellationToken);
        var family = await EnsureFamilyEnabledForTenantAsync(
            tenantId,
            request.FamilyId,
            cancellationToken);
        var attributes = AssetFamilyAttributeValidator.ValidateAndProject(
            family.FieldSchemaJson,
            request.Attributes);

        var baseLocation = request.BaseLocationName.Trim();

        var existingTags = await dbContext.Assets
            .AsNoTracking()
            .Where(a => a.TenantId == tenantId && plan.Tags.Contains(a.Tag))
            .Select(a => a.Tag)
            .ToListAsync(cancellationToken);

        if (existingTags.Count > 0)
        {
            throw new AssetTagDuplicateException(
                $"One or more tags already exist: {string.Join(", ", existingTags)}");
        }

        var assets = new List<Asset>(plan.Tags.Count);

        foreach (var tag in plan.Tags)
        {
            var asset = new Asset
            {
                TenantId = tenantId,
                UnitId = request.UnitId,
                CategoryId = category.Id,
                FamilyId = family.Id,
                Name = $"{category.Name} {tag}",
                Tag = tag,
                Location = string.IsNullOrWhiteSpace(baseLocation) ? null : baseLocation,
                Status = AssetStatus.Active,
                IsRentable = request.IsRentable,
                RequiresMaintenance = request.RequiresMaintenance,
                Attributes = new Dictionary<string, string?>(attributes, StringComparer.Ordinal),
            };

            SyncRentalConfiguration(
                asset,
                request.IsRentable,
                plan.RentalType,
                plan.TotalQuantity,
                request.RequiresDeposit,
                request.QueueEnabled,
                request.QueueOpeningTime);

            assets.Add(asset);
        }

        await dbContext.Assets.AddRangeAsync(assets, cancellationToken);
        await SaveChangesProtectingTagAsync(cancellationToken);

        var responses = assets
            .OrderBy(a => a.Tag)
            .Select(ToResponse)
            .ToList();

        return new BulkCreateAssetsResponse(responses.Count, responses);
    }

    public Task<AssetResponse> CreateRentableAsync(
        CreateRentableRequest request,
        CancellationToken cancellationToken)
    {
        return CreateAsync(
            new CreateAssetRequest
            {
                UnitId = request.UnitId,
                CategoryId = request.CategoryId,
                FamilyId = request.FamilyId,
                Name = request.Name,
                Tag = request.Tag,
                Location = request.Location,
                Status = AssetStatus.Active,
                IsRentable = true,
                RequiresMaintenance = false,
                RentalType = request.RentalType,
                TotalQuantity = request.TotalQuantity,
                RequiresDeposit = request.RequiresDeposit,
                QueueEnabled = request.QueueEnabled,
                QueueOpeningTime = request.QueueOpeningTime,
                VisualTemplateRevisionId = request.VisualTemplateRevisionId,
                CapacityPeople = request.CapacityPeople,
                ConfirmVisualSuggestions = request.ConfirmVisualSuggestions,
                MaxAdvanceBookingDays = request.MaxAdvanceBookingDays,
            },
            cancellationToken);
    }

    public async Task<AssetResponse> UpdateRentableAsync(
        Guid id,
        UpdateRentableRequest request,
        CancellationToken cancellationToken)
    {
        var tenantId = EnsureTenantContext();

        var asset = await dbContext.Assets
            .AsNoTracking()
            .FirstOrDefaultAsync(a => a.Id == id && a.TenantId == tenantId, cancellationToken);

        if (asset is null || !asset.IsRentable)
        {
            throw new KeyNotFoundException($"Rentable asset '{id}' was not found.");
        }

        var updated = await UpdateAsync(
            id,
            new UpdateAssetRequest
            {
                UnitId = request.UnitId,
                CategoryId = request.CategoryId,
                FamilyId = request.FamilyId,
                Name = request.Name,
                Tag = request.Tag,
                Location = request.Location,
                SerialNumber = asset.SerialNumber,
                InstallationDate = asset.InstallationDate,
                Status = asset.Status,
                IsRentable = true,
                RequiresMaintenance = asset.RequiresMaintenance,
                RentalType = request.RentalType,
                TotalQuantity = request.TotalQuantity,
                RequiresDeposit = request.RequiresDeposit,
                QueueEnabled = request.QueueEnabled,
                QueueOpeningTime = request.QueueOpeningTime,
                VisualTemplateRevisionId = request.VisualTemplateRevisionId,
                CapacityPeople = request.CapacityPeople,
                ConfirmVisualSuggestions = request.ConfirmVisualSuggestions,
                MaxAdvanceBookingDays = request.MaxAdvanceBookingDays,
                Attributes = asset.Attributes,
            },
            cancellationToken);

        return updated
            ?? throw new KeyNotFoundException($"Rentable asset '{id}' was not found.");
    }

    private async Task<ResourceVisualTemplateRevision?> ValidateVisualAssignmentAsync(
        bool isRentable, JsonOptional<Guid> requested, Guid? existingRevisionId, CancellationToken ct)
    {
        var revisionId = requested.IsSpecified ? requested.Value : existingRevisionId;
        if (!isRentable || revisionId is null) return null;
        if (revisionId == Guid.Empty)
            throw new ResourceVisualException("VISUAL_TEMPLATE_INVALID", "A published visual revision is required.");
        var isNewChoice = requested.IsSpecified && revisionId != existingRevisionId;
        if (isNewChoice && dbContext.Database.ProviderName?.Contains("Npgsql", StringComparison.Ordinal) == true)
        {
            // SHARE conflicts with catalog archival UPDATE, so assignment linearizes while active.
            await dbContext.Database.ExecuteSqlInterpolatedAsync($"""
                SELECT 1 FROM core.resource_visual_templates AS t
                JOIN core.resource_visual_template_revisions AS r ON r.template_id = t.id
                WHERE r.id = {revisionId.Value} FOR SHARE OF t
                """, ct);
        }
        var revision = await dbContext.Set<ResourceVisualTemplateRevision>().AsNoTracking()
            .SingleOrDefaultAsync(r => r.Id == revisionId && r.Status == ResourceVisualRevisionStatus.Published, ct);
        if (revision is null)
            throw new ResourceVisualException("VISUAL_TEMPLATE_INVALID", "Visual revision must exist and be published.");
        if (isNewChoice && !await dbContext.Set<ResourceVisualTemplate>().AsNoTracking()
            .AnyAsync(t => t.Id == revision.TemplateId && t.IsActive, ct))
            throw new ResourceVisualException("VISUAL_TEMPLATE_ARCHIVED", "Archived templates cannot be selected.");
        return revision;
    }

    private static void ValidateCapacity(int? value)
    {
        if (value is <= 0 or > 100000)
            throw new ResourceVisualException("VISUAL_TEMPLATE_INVALID", "People capacity must be between 1 and 100000.");
    }

    private static void ApplyVisualConfiguration(Asset asset, JsonOptional<Guid> revision,
        JsonOptional<int> capacity, bool confirmSuggestions, JsonOptional<int> horizon,
        ResourceVisualTemplateRevision? selected)
    {
        if (!asset.IsRentable || asset.RentalConfiguration is not { } rental) return;
        if (revision.IsSpecified) rental.VisualTemplateRevisionId = revision.Value;
        if (capacity.IsSpecified) rental.CapacityPeople = capacity.Value;
        if (confirmSuggestions && rental.CapacityPeople is null && !capacity.IsSpecified)
            rental.CapacityPeople = selected?.SuggestedCapacityPeople;
        if (horizon.IsSpecified) rental.MaxAdvanceBookingDays = horizon.Value;
        // Duration suggestions are exposed by the picker for explicit schedule configuration.
    }

    private void AuditVisualAssignment(Asset asset) => logger?.LogInformation(
        "ResourceVisualAudit Action={Action} TenantId={TenantId} RentalAssetId={RentalAssetId} RevisionId={RevisionId}",
        "resource.visual.saved", asset.TenantId, asset.RentalConfiguration?.Id, asset.RentalConfiguration?.VisualTemplateRevisionId);

    private void SyncRentalConfiguration(
        Asset asset,
        bool isRentable,
        RentalAssetType rentalType,
        int totalQuantity,
        bool requiresDeposit,
        bool queueEnabled,
        TimeOnly? queueOpeningTime)
    {
        if (!isRentable)
        {
            if (asset.RentalConfiguration is not null)
            {
                asset.RentalConfiguration.IsActive = false;
                asset.RentalConfiguration.Touch();
            }

            return;
        }

        var enabled = queueEnabled && rentalType == RentalAssetType.Location;
        var openingTime = enabled ? queueOpeningTime : null;

        if (asset.RentalConfiguration is null)
        {
            asset.RentalConfiguration = new RentalAsset
            {
                TenantId = asset.TenantId,
                AssetId = asset.Id,
                Type = rentalType,
                TotalQuantity = totalQuantity,
                IsActive = true,
                RequiresDeposit = requiresDeposit,
                SchedulePolicy = SchedulePolicy.SlotGrid,
                QueueEnabled = enabled,
                QueueOpeningTime = openingTime,
            };
            return;
        }

        asset.RentalConfiguration.Type = rentalType;
        asset.RentalConfiguration.TotalQuantity = totalQuantity;
        asset.RentalConfiguration.RequiresDeposit = requiresDeposit;
        asset.RentalConfiguration.QueueEnabled = enabled;
        asset.RentalConfiguration.QueueOpeningTime = openingTime;
        asset.RentalConfiguration.IsActive = true;
        asset.RentalConfiguration.Touch();
    }

    private static void ValidateQueueFields(
        bool isRentable,
        RentalAssetType rentalType,
        bool queueEnabled,
        TimeOnly? queueOpeningTime)
    {
        if (!isRentable || !queueEnabled)
        {
            return;
        }

        if (rentalType != RentalAssetType.Location)
        {
            throw new ArgumentException("Waiting queue can only be enabled for Location rentals.");
        }

        if (queueOpeningTime is null)
        {
            throw new ArgumentException(
                "QueueOpeningTime is required when the waiting queue is enabled.");
        }
    }

    private static void ValidateRentalFields(
        bool isRentable,
        RentalAssetType rentalType,
        int totalQuantity)
    {
        if (!isRentable)
        {
            return;
        }

        if (totalQuantity < 1)
        {
            throw new ArgumentException("TotalQuantity must be at least 1 when IsRentable is true.");
        }

        if (rentalType == RentalAssetType.Location && totalQuantity != 1)
        {
            throw new ArgumentException("Location rentals must have TotalQuantity equal to 1.");
        }
    }

    private async Task EnsureTagAvailableAsync(
        string tag,
        Guid? excludeAssetId,
        CancellationToken cancellationToken)
    {
        var duplicate = await dbContext.Assets
            .AsNoTracking()
            .AnyAsync(
                asset => asset.TenantId == tenantProvider.TenantId && asset.Tag == tag && (excludeAssetId == null || asset.Id != excludeAssetId),
                cancellationToken);

        if (duplicate)
        {
            throw new AssetTagDuplicateException();
        }
    }

    private async Task SaveChangesProtectingTagAsync(CancellationToken cancellationToken)
    {
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (AssetPersistenceConflicts.IsTagUniqueViolation(ex))
        {
            dbContext.ChangeTracker.Clear();
            throw new AssetTagDuplicateException();
        }
    }

    private async Task EnsureUnitExistsAsync(Guid unitId, CancellationToken cancellationToken)
    {
        var exists = await dbContext.Units
            .AsNoTracking()
            .AnyAsync(u => u.Id == unitId && u.TenantId == tenantProvider.TenantId, cancellationToken);

        if (!exists)
        {
            throw new KeyNotFoundException($"Unit '{unitId}' was not found.");
        }
    }

    private async Task<AssetCategory> EnsureCategoryExistsAsync(
        Guid categoryId,
        CancellationToken cancellationToken)
    {
        var category = await dbContext.AssetCategories
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == categoryId && c.TenantId == tenantProvider.TenantId, cancellationToken);

        if (category is null)
        {
            throw new KeyNotFoundException($"Asset category '{categoryId}' was not found.");
        }

        return category;
    }

    private async Task<AssetFamily> EnsureFamilyEnabledForTenantAsync(
        Guid tenantId,
        Guid familyId,
        CancellationToken cancellationToken)
    {
        var family = await dbContext.AssetFamilies
            .AsNoTracking()
            .FirstOrDefaultAsync(f => f.Id == familyId && f.IsActive, cancellationToken);

        if (family is null)
        {
            throw new KeyNotFoundException($"Asset family '{familyId}' was not found.");
        }

        var enabled = await dbContext.TenantAssetFamilies
            .AsNoTracking()
            .AnyAsync(t => t.TenantId == tenantId && t.FamilyId == familyId, cancellationToken);

        if (!enabled)
        {
            throw new ArgumentException(
                $"Asset family '{family.Key}' is not enabled for this tenant.");
        }

        return family;
    }

    private Guid EnsureTenantContext()
    {
        return tenantProvider.TenantId is { } id && id != Guid.Empty
            ? id : throw new UnauthorizedAccessException("Tenant context is required.");
    }

    private static BulkCreatePlan ResolveBulkCreatePlan(BulkCreateAssetsRequest request)
    {
        var baseTag = request.BaseTag.Trim();
        var rentalType = request.RentalType;

        if (rentalType == RentalAssetType.Location)
        {
            if (request.StartNumber is null || request.EndNumber is null)
            {
                throw new ArgumentException(
                    "StartNumber and EndNumber are required when RentalType is Location.");
            }

            var startNumber = request.StartNumber.Value;
            var endNumber = request.EndNumber.Value;

            if (startNumber > endNumber)
            {
                throw new ArgumentException("StartNumber must be less than or equal to EndNumber.");
            }

            var createCount = (long)endNumber - startNumber + 1;
            EnsureWithinBulkCreateLimit(createCount);

            var tags = new List<string>((int)createCount);
            for (var number = startNumber; number <= endNumber; number++)
            {
                tags.Add(BuildTag(baseTag, number));
            }

            return new BulkCreatePlan(rentalType, 1, tags);
        }

        if (rentalType == RentalAssetType.Good)
        {
            EnsureWithinBulkCreateLimit(1);
            return new BulkCreatePlan(rentalType, request.TotalQuantity, [baseTag]);
        }

        throw new ArgumentException($"Unsupported RentalType '{rentalType}'.");
    }

    private static void EnsureWithinBulkCreateLimit(long createCount)
    {
        if (createCount > MaxBulkCreateCount)
        {
            throw new ArgumentException(
                $"Bulk create is limited to {MaxBulkCreateCount} assets per request.");
        }
    }

    private static string BuildTag(string baseTag, int number)
    {
        return baseTag.EndsWith('-') || baseTag.EndsWith('_')
            ? $"{baseTag}{number}"
            : $"{baseTag}-{number}";
    }

    private sealed record BulkCreatePlan(
        RentalAssetType RentalType,
        int TotalQuantity,
        List<string> Tags);

    private static string? NormalizeOptional(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return value.Trim();
    }

    private static AssetResponse ToResponse(Asset asset)
    {
        AssetRentalConfigResponse? rentalConfig = null;

        if (asset.RentalConfiguration is not null)
        {
            rentalConfig = new AssetRentalConfigResponse(
                asset.RentalConfiguration.Id,
                asset.RentalConfiguration.Type,
                asset.RentalConfiguration.TotalQuantity,
                asset.RentalConfiguration.IsActive,
                asset.RentalConfiguration.RequiresDeposit,
                asset.RentalConfiguration.QueueEnabled,
                asset.RentalConfiguration.QueueOpeningTime,
                asset.RentalConfiguration.VisualTemplateRevisionId,
                asset.RentalConfiguration.CapacityPeople);
        }

        return new(
            asset.Id,
            asset.TenantId,
            asset.UnitId,
            asset.CategoryId,
            asset.FamilyId,
            asset.Name,
            asset.Tag,
            asset.Location,
            asset.SerialNumber,
            asset.InstallationDate,
            asset.Status,
            asset.IsRentable,
            asset.RequiresMaintenance,
            asset.Attributes,
            rentalConfig,
            asset.CreatedAt,
            asset.UpdatedAt,
            asset.ScheduledDeletionAt);
    }
}
