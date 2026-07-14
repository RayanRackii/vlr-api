using Platform.Core.Domain.Common;

namespace Platform.Core.Domain.Entities;

/// <summary>
/// Schema marker for the inventory module (current table: assets.assets).
/// Full properties already live on <see cref="Asset"/>; keep TenantId as the isolation key.
/// Target schema rename to <c>inventory</c> is a deferred migration.
/// </summary>
public interface IInventoryModuleEntity : ITenantScoped;
