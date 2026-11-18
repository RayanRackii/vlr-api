namespace Platform.Core.Domain.Enums;

/// <summary>
/// What a catalog product price is charged per ("R$ 45,00 por hora"). A closed list on purpose:
/// no free text. A null unit on the product means the price is shown on its own.
/// Stored as text.
/// </summary>
public enum CatalogProductPriceUnit
{
    Unit = 0,
    Hour = 1,
    Day = 2,
    Person = 3,
    Booking = 4,
    Package = 5,
}
