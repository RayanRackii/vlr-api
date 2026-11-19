using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Platform.Api.Modules.Company.Dtos;
using Platform.Api.Services.Trial;
using Platform.Api.Storage;
using Platform.Core.Domain.Entities;
using Platform.Core.Infrastructure.Persistence;

namespace Platform.Api.Modules.Company.Services;

public interface IPortalAppearanceService
{
    Task<PortalAppearanceResponse> GetAsync(CancellationToken cancellationToken);

    Task<PortalAppearanceResponse> UpdateTextsAsync(
        UpdatePortalAppearanceRequest request,
        CancellationToken cancellationToken);

    Task<PortalAppearanceResponse> UploadLandingImageAsync(
        string fileName,
        string contentType,
        Stream content,
        CancellationToken cancellationToken);

    Task<PortalAppearanceResponse> DeleteLandingImageAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Customer portal login appearance of the CURRENT tenant only. Validation errors surface as
/// <see cref="ArgumentException"/> (400); a read-only trial as <see cref="InvalidOperationException"/> (409).
/// The image lives in the public bucket; replaced or removed objects are deleted best-effort after the commit.
/// </summary>
public sealed class PortalAppearanceService(
    AppDbContext dbContext,
    ITenantProvider tenantProvider,
    ITrialGuard trialGuard,
    IStorageProvider storageProvider,
    IOptions<StorageOptions> storageOptions,
    ILogger<PortalAppearanceService> logger) : IPortalAppearanceService
{
    public async Task<PortalAppearanceResponse> GetAsync(CancellationToken cancellationToken)
    {
        var tenantId = RequireTenant();
        var tenant = await dbContext.Tenants.AsNoTracking()
            .FirstOrDefaultAsync(item => item.Id == tenantId, cancellationToken)
            ?? throw new KeyNotFoundException("Company not found.");
        return Map(tenant);
    }

    public async Task<PortalAppearanceResponse> UpdateTextsAsync(
        UpdatePortalAppearanceRequest request,
        CancellationToken cancellationToken)
    {
        var tenantId = RequireTenant();
        await trialGuard.EnsureWritableAsync(cancellationToken);

        var tenant = await dbContext.Tenants
            .FirstOrDefaultAsync(item => item.Id == tenantId, cancellationToken)
            ?? throw new KeyNotFoundException("Company not found.");

        tenant.UpdateLandingTexts(request.LandingKicker, request.LandingHeadline, request.LandingSubtitle);
        await dbContext.SaveChangesAsync(cancellationToken);
        return Map(tenant);
    }

    public async Task<PortalAppearanceResponse> UploadLandingImageAsync(
        string fileName,
        string contentType,
        Stream content,
        CancellationToken cancellationToken)
    {
        var tenantId = RequireTenant();
        await trialGuard.EnsureWritableAsync(cancellationToken);

        using var buffer = new MemoryStream();
        await content.CopyToAsync(buffer, cancellationToken);
        var bytes = buffer.ToArray();
        var extension = PortalLandingImageRules.Validate(fileName, contentType, bytes.Length, bytes);

        var tenant = await dbContext.Tenants
            .FirstOrDefaultAsync(item => item.Id == tenantId, cancellationToken)
            ?? throw new KeyNotFoundException("Company not found.");

        var bucket = storageOptions.Value.PublicBucket;
        var previousKey = tenant.LandingImageKey;
        var newKey = PortalLandingImageRules.StorageKey(tenantId, Guid.NewGuid(), extension);

        buffer.Position = 0;
        await storageProvider.UploadAsync(bucket, newKey, buffer, contentType, cancellationToken);

        try
        {
            tenant.SetLandingImageKey(newKey);
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch
        {
            await TryDeleteAsync(bucket, newKey);
            throw;
        }

        if (previousKey is not null && previousKey != newKey)
        {
            await TryDeleteAsync(bucket, previousKey);
        }

        return Map(tenant);
    }

    public async Task<PortalAppearanceResponse> DeleteLandingImageAsync(CancellationToken cancellationToken)
    {
        var tenantId = RequireTenant();
        await trialGuard.EnsureWritableAsync(cancellationToken);

        var tenant = await dbContext.Tenants
            .FirstOrDefaultAsync(item => item.Id == tenantId, cancellationToken)
            ?? throw new KeyNotFoundException("Company not found.");

        var previousKey = tenant.LandingImageKey;
        if (previousKey is null)
        {
            return Map(tenant);
        }

        tenant.SetLandingImageKey(null);
        await dbContext.SaveChangesAsync(cancellationToken);
        await TryDeleteAsync(storageOptions.Value.PublicBucket, previousKey);
        return Map(tenant);
    }

    private async Task TryDeleteAsync(string bucket, string key)
    {
        try
        {
            await storageProvider.DeleteAsync(bucket, key, CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not delete portal landing object {Key} from bucket {Bucket}.", key, bucket);
        }
    }

    private Guid RequireTenant() =>
        tenantProvider.TenantId is Guid id && id != Guid.Empty
            ? id
            : throw new UnauthorizedAccessException("Tenant context is required.");

    private PortalAppearanceResponse Map(Tenant tenant) => new(
        tenant.LandingImageKey is null
            ? null
            : storageProvider.GetPublicUrl(storageOptions.Value.PublicBucket, tenant.LandingImageKey),
        tenant.LandingKicker,
        tenant.LandingHeadline,
        tenant.LandingSubtitle,
        tenant.WelcomeTagline);
}
