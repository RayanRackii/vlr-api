using System.Text.Json;
using Platform.Core.Infrastructure.Supabase;

namespace Platform.Api.Tests.Authentication;

public sealed class SupabaseUserTenantMetadataTests
{
    [Fact]
    public void Reads_a_string_tenant_id()
    {
        var tenantId = Guid.NewGuid();
        using var document = JsonDocument.Parse(
            $"{{\"app_metadata\":{{\"tenant_id\":\"{tenantId}\"}}}}");

        Assert.Equal(tenantId, SupabaseUserTenantMetadata.ReadTenantId(document.RootElement));
    }

    [Theory]
    [InlineData("""{"app_metadata":{}}""")]
    [InlineData("""{"app_metadata":{"tenant_id":null}}""")]
    [InlineData("""{"app_metadata":{"tenant_id":""}}""")]
    [InlineData("""{"app_metadata":{"tenant_id":"not-a-guid"}}""")]
    [InlineData("""{"app_metadata":{"tenant_id":"00000000-0000-0000-0000-000000000000"}}""")]
    [InlineData("""{"id":"user"}""")]
    public void Missing_or_unusable_tenant_id_is_null(string json)
    {
        using var document = JsonDocument.Parse(json);

        Assert.Null(SupabaseUserTenantMetadata.ReadTenantId(document.RootElement));
    }

    [Fact]
    public void Non_string_tenant_id_is_null()
    {
        using var document = JsonDocument.Parse("""{"app_metadata":{"tenant_id":12}}""");

        Assert.Null(SupabaseUserTenantMetadata.ReadTenantId(document.RootElement));
    }
}
