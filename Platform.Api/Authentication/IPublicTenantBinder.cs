using Platform.Core.Infrastructure.Persistence;

namespace Platform.Api.Authentication;

public interface IPublicTenantBinder
{
    /// <summary>
    /// Resolves tenant by subdomain and sets <see cref="AmbientTenantContext"/>.
    /// </summary>
    Task BindFromSubdomainAsync(string? subdomain, CancellationToken cancellationToken);
}
