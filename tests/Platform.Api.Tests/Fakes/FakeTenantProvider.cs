using Platform.Core.Infrastructure.Persistence;

namespace Platform.Api.Tests.Fakes;

public sealed class FakeTenantProvider : ITenantProvider
{
    public Guid? TenantId { get; set; }
}
