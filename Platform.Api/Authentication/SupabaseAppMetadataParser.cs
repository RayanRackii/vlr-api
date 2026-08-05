using System.Security.Claims;
using System.Text.Json;

namespace Platform.Api.Authentication;

internal static class SupabaseAppMetadataParser
{
    public static Guid? TryExtractTenantId(ClaimsPrincipal user)
    {
        var appMetadataClaim = user.FindFirst(TenantClaimTypes.AppMetadata)?.Value;

        if (string.IsNullOrWhiteSpace(appMetadataClaim))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(appMetadataClaim);

            if (!document.RootElement.TryGetProperty(TenantClaimTypes.TenantId, out var tenantIdElement))
            {
                return null;
            }

            if (tenantIdElement.ValueKind == JsonValueKind.Null)
            {
                return null;
            }

            if (tenantIdElement.ValueKind != JsonValueKind.String)
            {
                return null;
            }

            var tenantIdValue = tenantIdElement.GetString();

            if (string.IsNullOrWhiteSpace(tenantIdValue)
                || !Guid.TryParse(tenantIdValue, out var tenantId))
            {
                return null;
            }

            return tenantId;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static Guid ExtractTenantId(ClaimsPrincipal user)
    {
        return TryExtractTenantId(user)
            ?? throw new TenantResolutionException(
                "The access token is missing a valid app_metadata.tenant_id.");
    }
}
