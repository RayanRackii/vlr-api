using Platform.Core.Domain.Common;

namespace Platform.Core.Domain.Entities;

/// <summary>
/// Extra registration field defined by a tenant (beyond core name/email/password/phone).
/// Schema: core.tenant_registration_fields.
/// </summary>
public class TenantRegistrationField : Entity, ITenantScoped
{
    public Guid TenantId { get; private set; }

    /// <summary>Stable key stored in Customer.ExtraAttributes (snake or camel, unique per tenant).</summary>
    public string FieldKey { get; private set; } = null!;

    public string Label { get; private set; } = null!;

    /// <summary>See <see cref="Constants.RegistrationFieldTypes"/>.</summary>
    public string FieldType { get; private set; } = null!;

    public bool IsRequired { get; private set; }

    public int SortOrder { get; private set; }

    /// <summary>JSON array of options for select fields, e.g. ["A","B"].</summary>
    public string? OptionsJson { get; private set; }

    public Tenant? Tenant { get; private set; }

    private TenantRegistrationField()
    {
    }

    public TenantRegistrationField(
        Guid tenantId,
        string fieldKey,
        string label,
        string fieldType,
        bool isRequired,
        int sortOrder,
        string? optionsJson = null)
    {
        TenantId = tenantId;
        FieldKey = NormalizeKey(fieldKey);
        Label = NormalizeLabel(label);
        FieldType = fieldType;
        IsRequired = isRequired;
        SortOrder = sortOrder;
        OptionsJson = string.IsNullOrWhiteSpace(optionsJson) ? null : optionsJson.Trim();
    }

    public void Update(
        string label,
        string fieldType,
        bool isRequired,
        int sortOrder,
        string? optionsJson)
    {
        Label = NormalizeLabel(label);
        FieldType = fieldType;
        IsRequired = isRequired;
        SortOrder = sortOrder;
        OptionsJson = string.IsNullOrWhiteSpace(optionsJson) ? null : optionsJson.Trim();
        MarkAsUpdated();
    }

    private static string NormalizeKey(string key)
    {
        var trimmed = key.Trim();
        if (trimmed.Length is < 2 or > 64)
        {
            throw new ArgumentException("FieldKey must be between 2 and 64 characters.");
        }

        if (!trimmed.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-'))
        {
            throw new ArgumentException(
                "FieldKey must contain only letters, digits, underscore, or hyphen.");
        }

        return trimmed;
    }

    private static string NormalizeLabel(string label)
    {
        var trimmed = label.Trim();
        if (trimmed.Length is < 1 or > 120)
        {
            throw new ArgumentException("Label must be between 1 and 120 characters.");
        }

        return trimmed;
    }
}
