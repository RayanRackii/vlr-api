namespace Platform.Api.Modules.Payments;

/// <summary>Bound from the <c>Payments</c> section. Keys are versioned: <c>Payments:EncryptionKeys:v1</c> = base64 of 32 bytes.</summary>
public sealed class PaymentsOptions
{
    public const string SectionName = "Payments";

    public Dictionary<string, string> EncryptionKeys { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}
