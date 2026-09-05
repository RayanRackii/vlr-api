using Platform.Core.Domain.Constants;
using Platform.Core.Domain.Services;

namespace Platform.Api.Tests.Trial;

public sealed class TrialSubdomainGeneratorTests
{
    [Fact]
    public async Task AllocateAsync_skips_reserved_mail_from_Mailbox_Club()
    {
        var allocated = await TrialSubdomainGenerator.AllocateAsync(
            "Mailbox Club",
            _ => Task.FromResult(false));

        Assert.NotEqual("mail", allocated);
        Assert.False(ReservedTenantSubdomains.IsReserved(allocated));
    }
}
