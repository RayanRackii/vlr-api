namespace Platform.Api.Modules.Rentals;

/// <summary>The current server total differs from the customer's quoted total.</summary>
public sealed class PriceChangedException(decimal currentTotalAmount)
    : InvalidOperationException("O preço mudou. Revise antes de continuar.")
{
    public const string Code = "PRICE_CHANGED";

    public decimal CurrentTotalAmount { get; } = currentTotalAmount;
}
