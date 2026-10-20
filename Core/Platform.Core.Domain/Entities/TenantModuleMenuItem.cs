using Platform.Core.Domain.Common;
using Platform.Core.Domain.Constants;

namespace Platform.Core.Domain.Entities;

/// <summary>
/// Configurable B2C sidebar entry for an active tenant module (multiple per module).
/// Schema: core.tenant_module_menu_items.
/// </summary>
public class TenantModuleMenuItem : Entity, ITenantScoped
{
    public Guid TenantId { get; private set; }

    /// <summary>Canonical module key (see <see cref="PlatformModules"/>).</summary>
    public string ModuleName { get; private set; } = null!;

    public string Label { get; private set; } = null!;

    public int SortOrder { get; private set; }

    public bool IsActive { get; private set; }

    /// <summary>
    /// Optional rentals target: <see cref="RentalAsset.Id"/>. Null = general agenda.
    /// </summary>
    public Guid? RentalAssetId { get; private set; }

    /// <summary>Booking inactivity timeout in minutes. Null uses the portal default.</summary>
    public int? BookingIdleTimeoutMinutes { get; private set; }

    /// <summary>Local path after booking inactivity. Null uses the portal default.</summary>
    public string? BookingIdleRedirectPath { get; private set; }

    public Tenant? Tenant { get; private set; }

    public RentalAsset? RentalAsset { get; private set; }

    private TenantModuleMenuItem()
    {
    }

    public TenantModuleMenuItem(
        Guid tenantId,
        string moduleName,
        string label,
        int sortOrder,
        bool isActive = true,
        Guid? rentalAssetId = null,
        int? bookingIdleTimeoutMinutes = null,
        string? bookingIdleRedirectPath = null)
    {
        ValidateBookingIdleSettings(bookingIdleTimeoutMinutes, bookingIdleRedirectPath);
        TenantId = tenantId;
        ModuleName = NormalizeModule(moduleName);
        Label = NormalizeLabel(label);
        SortOrder = sortOrder;
        IsActive = isActive;
        RentalAssetId = rentalAssetId;
        BookingIdleTimeoutMinutes = bookingIdleTimeoutMinutes;
        BookingIdleRedirectPath = bookingIdleRedirectPath;
        EnsureRentalAssetRules();
    }

    public void Update(
        string label,
        int sortOrder,
        bool isActive,
        Guid? rentalAssetId,
        int? bookingIdleTimeoutMinutes = null,
        string? bookingIdleRedirectPath = null)
    {
        ValidateBookingIdleSettings(bookingIdleTimeoutMinutes, bookingIdleRedirectPath);
        Label = NormalizeLabel(label);
        SortOrder = sortOrder;
        IsActive = isActive;
        RentalAssetId = rentalAssetId;
        BookingIdleTimeoutMinutes = bookingIdleTimeoutMinutes;
        BookingIdleRedirectPath = bookingIdleRedirectPath;
        EnsureRentalAssetRules();
        MarkAsUpdated();
    }

    private static void ValidateBookingIdleSettings(int? timeoutMinutes, string? redirectPath)
    {
        if (timeoutMinutes is < 1 or > 120)
        {
            throw new ArgumentException("BookingIdleTimeoutMinutes must be between 1 and 120, or null to use the default.");
        }

        if (redirectPath is not null
            && (redirectPath.Length > 200
                || !redirectPath.StartsWith('/')
                || redirectPath.StartsWith("//", StringComparison.Ordinal)
                || redirectPath.Contains('\\')
                || redirectPath.Any(char.IsControl)))
        {
            throw new ArgumentException(
                "BookingIdleRedirectPath must be a relative path starting with '/' (not '//'), at most 200 characters, without backslashes or control characters, or null to use the default.");
        }
    }

    private void EnsureRentalAssetRules()
    {
        if (RentalAssetId.HasValue && ModuleName != PlatformModules.Rentals)
        {
            throw new ArgumentException(
                "RentalAssetId is only allowed when ModuleName is rentals.");
        }
    }

    private static string NormalizeModule(string moduleName)
    {
        if (!PlatformModules.TryNormalize(moduleName, out var canonical))
        {
            throw new ArgumentException($"Unknown module '{moduleName}'.");
        }

        return canonical;
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
