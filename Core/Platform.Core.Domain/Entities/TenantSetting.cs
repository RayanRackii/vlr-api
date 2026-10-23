using Platform.Core.Domain.Common;

namespace Platform.Core.Domain.Entities;

/// <summary>
/// One tenant-scoped key/value setting. Keys live in
/// <see cref="Constants.TenantSettingKeys"/>; a missing row means the platform default.
/// </summary>
public class TenantSetting : Entity, ITenantScoped
{
    public const int KeyMaxLength = 100;

    public required Guid TenantId { get; set; }

    public required string Key { get; set; }

    public required string Value { get; set; }
}
