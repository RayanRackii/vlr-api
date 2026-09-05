using Platform.Api.Features.CreateTenant;
using Platform.Core.Domain.Constants;

namespace Platform.Api.Tests.Admin;

public sealed class CreateTenantTrialProvisioningTests
{
    [Fact]
    public void Trial_modules_that_include_pmoc_must_have_a_provisioning_family()
    {
        if (!CreateTenantHandler.TrialModules.Contains(PlatformModules.Pmoc, StringComparer.Ordinal))
        {
            return;
        }

        Assert.True(
            AssetCategoryExampleSeeds.HasPmocProvisioningFamily(CreateTenantHandler.TrialFamilyKeys));
    }
}
