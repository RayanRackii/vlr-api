using System.Security.Claims;
using System.Text.Json;

namespace Platform.Api.Authentication;

internal static class SupabaseAppMetadataParser
{
    public static Guid ExtractTenantId(ClaimsPrincipal user)
    {
        var appMetadataClaim = user.FindFirst(TenantClaimTypes.AppMetadata)?.Value;

        if (string.IsNullOrWhiteSpace(appMetadataClaim))
        {
            throw new TenantResolutionException(
                "The access token is missing the app_metadata claim.");
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(appMetadataClaim);
        }
        catch (JsonException ex)
        {
            throw new TenantResolutionException(
                "The app_metadata claim contains invalid JSON.",
                ex);
        }

        using (document)
        {
            if (!document.RootElement.TryGetProperty(TenantClaimTypes.TenantId, out var tenantIdElement))
            {
                throw new TenantResolutionException(
                    "The app_metadata claim does not contain tenant_id.");
            }

            if (tenantIdElement.ValueKind != JsonValueKind.String)
            {
                throw new TenantResolutionException(
                    "The tenant_id value in app_metadata must be a string.");
            }

            var tenantIdValue = tenantIdElement.GetString();

            if (!Guid.TryParse(tenantIdValue, out var tenantId))
            {
                throw new TenantResolutionException(
                    "The tenant_id value in app_metadata is not a valid GUID.");
            }

            return tenantId;
        }
    }
}
