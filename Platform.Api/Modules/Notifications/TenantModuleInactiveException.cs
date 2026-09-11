using Platform.Api.Authorization;

namespace Platform.Api.Modules.Notifications;

public sealed class TenantModuleInactiveException()
    : InvalidOperationException(RequireActiveModuleAttribute.InactiveModuleError);
