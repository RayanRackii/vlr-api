using Platform.Api.Authorization;

namespace Platform.Api.Modules.Notifications;

public sealed class TenantModuleInactiveException(string moduleKey)
    : InvalidOperationException(RequireActiveModuleAttribute.InactiveModuleError)
{
    public string ModuleKey { get; } = moduleKey;
}
