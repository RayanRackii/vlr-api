namespace Platform.Core.Domain.Services;

public static class CatalogMoney
{
    public static decimal Round(decimal value) =>
        decimal.Round(value, 2, MidpointRounding.AwayFromZero);
}
