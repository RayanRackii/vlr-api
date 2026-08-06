namespace Platform.Core.Domain.Constants;

public static class AssetFamilyKeys
{
    public const string Spaces = "spaces";
    public const string Electrical = "electrical";
    public const string Goods = "goods";
    public const string Generic = "generic";

    /// <summary>Stable ids used by seed migration and runtime lookups.</summary>
    public static class Ids
    {
        public static readonly Guid Spaces = Guid.Parse("11111111-1111-1111-1111-111111111101");
        public static readonly Guid Electrical = Guid.Parse("11111111-1111-1111-1111-111111111102");
        public static readonly Guid Goods = Guid.Parse("11111111-1111-1111-1111-111111111103");
        public static readonly Guid Generic = Guid.Parse("11111111-1111-1111-1111-111111111104");
    }
}
