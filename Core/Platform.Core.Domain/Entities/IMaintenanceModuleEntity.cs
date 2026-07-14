using Platform.Core.Domain.Common;

namespace Platform.Core.Domain.Entities;

/// <summary>
/// Schema marker for the maintenance module (current tables: pmoc.*, os.*).
/// Full properties already live on <see cref="MaintenancePlan"/> / <see cref="WorkOrder"/>.
/// Target schema rename to <c>maintenance</c> is a deferred migration.
/// </summary>
public interface IMaintenanceModuleEntity : ITenantScoped;
