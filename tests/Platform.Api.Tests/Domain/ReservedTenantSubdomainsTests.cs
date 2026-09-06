using Platform.Core.Domain.Constants;

namespace Platform.Api.Tests.Domain;

public sealed class ReservedTenantSubdomainsTests
{
    [Fact]
    public void IsReserved_API_is_true()
    {
        Assert.True(ReservedTenantSubdomains.IsReserved("API"));
    }

    [Fact]
    public void IsReserved_ficc_is_false()
    {
        Assert.False(ReservedTenantSubdomains.IsReserved("ficc"));
    }
}
