namespace Platform.Core.Domain.Enums;

/// <summary>
/// Controls who may see a catalog product in the customer portal.
/// <see cref="Public"/> is visible to everyone (including anonymous visitors);
/// <see cref="Restricted"/> is visible only to customers granted access directly
/// or through one of their groups.
/// </summary>
public enum CatalogProductVisibility
{
    Public = 0,
    Restricted = 1,
}
