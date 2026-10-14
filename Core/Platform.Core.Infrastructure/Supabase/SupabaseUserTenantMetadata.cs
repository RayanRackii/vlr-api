using System.Text.Json;

namespace Platform.Core.Infrastructure.Supabase;

public static class SupabaseUserTenantMetadata
{
    public static Guid? ReadTenantId(JsonElement user)
    {
        if (!user.TryGetProperty("app_metadata", out var appMetadata)
            || appMetadata.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (!appMetadata.TryGetProperty("tenant_id", out var tenantIdElement)
            || tenantIdElement.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var tenantIdValue = tenantIdElement.GetString();
        if (string.IsNullOrWhiteSpace(tenantIdValue)
            || !Guid.TryParse(tenantIdValue, out var tenantId)
            || tenantId == Guid.Empty)
        {
            return null;
        }

        return tenantId;
    }
}
