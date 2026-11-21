namespace Platform.Api.Modules.Assets.Services;

/// <summary>
/// Translation keys for the asset families and fields created by the initial seed (pt-BR text).
/// A key is returned only while the stored label still equals the seeded text, so a label an admin
/// edited, or a family created later, never gets a key and keeps showing its own text.
/// </summary>
public static class SeededAssetFamilyLabels
{
    private static readonly Dictionary<string, string> FamilyLabels = new(StringComparer.Ordinal)
    {
        ["spaces"] = "Espaços",
        ["electrical"] = "Equipamentos elétricos",
        ["goods"] = "Bens e itens",
        ["generic"] = "Genérico",
    };

    private static readonly Dictionary<(string Family, string Field), string> FieldLabels = new()
    {
        [("spaces", "capacity")] = "Capacidade",
        [("electrical", "manufacturer")] = "Fabricante",
        [("electrical", "voltage")] = "Voltagem",
        [("electrical", "model")] = "Modelo",
        [("goods", "quantity_hint")] = "Quantidade típica",
    };

    public static string? FamilyKey(string familyKey, string? label) =>
        FamilyLabels.TryGetValue(familyKey, out var seeded) && string.Equals(seeded, label, StringComparison.Ordinal)
            ? $"assetFamilies.{familyKey}.label"
            : null;

    public static string? FieldKey(string familyKey, string fieldKey, string? label) =>
        FieldLabels.TryGetValue((familyKey, fieldKey), out var seeded) && string.Equals(seeded, label, StringComparison.Ordinal)
            ? $"assetFamilies.{familyKey}.fields.{fieldKey}.label"
            : null;
}
