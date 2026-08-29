using Platform.Core.Domain.Common;

namespace Platform.Core.Domain.Entities;

public class CatalogOrderItem : Entity, ITenantScoped
{
    public required Guid TenantId { get; set; }

    public required Guid OrderId { get; set; }

    public required Guid ProductId { get; set; }

    public required string ProductNameSnapshot { get; set; }

    public string? ProductCodeSnapshot { get; set; }

    public decimal? UnitPriceSnapshot { get; set; }

    public string Currency { get; set; } = "BRL";

    public required int Quantity { get; set; }

    public decimal? SubTotal { get; set; }

    public CatalogOrder Order { get; set; } = null!;

    public CatalogProduct Product { get; set; } = null!;
}
