using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Options;

namespace Platform.Api.Authorization;

/// <summary>
/// Builds <c>perm:{key}</c> policies on demand (default B2B policy AND permission).
/// Unknown policies (Customer, PlatformAdmin) delegate to the default provider.
/// </summary>
public sealed class PermissionAuthorizationPolicyProvider : IAuthorizationPolicyProvider
{
    private readonly DefaultAuthorizationPolicyProvider _fallback;
    private readonly AuthorizationOptions _options;

    public PermissionAuthorizationPolicyProvider(IOptions<AuthorizationOptions> options)
    {
        _options = options.Value;
        _fallback = new DefaultAuthorizationPolicyProvider(options);
    }

    public Task<AuthorizationPolicy> GetDefaultPolicyAsync() =>
        _fallback.GetDefaultPolicyAsync();

    public Task<AuthorizationPolicy?> GetFallbackPolicyAsync() =>
        _fallback.GetFallbackPolicyAsync();

    public Task<AuthorizationPolicy?> GetPolicyAsync(string policyName)
    {
        if (PermissionPolicies.TryParse(policyName, out var permissionKey))
        {
            var policy = new AuthorizationPolicyBuilder()
                .Combine(_options.DefaultPolicy)
                .AddRequirements(new PermissionRequirement(permissionKey))
                .Build();

            return Task.FromResult<AuthorizationPolicy?>(policy);
        }

        return _fallback.GetPolicyAsync(policyName);
    }
}
