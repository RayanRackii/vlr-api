namespace Platform.Api.Modules.Admin.Dtos;

/// <summary>One allow-listed tenant setting. <see cref="Value"/> is null when the tenant has no row.</summary>
public sealed record TenantSettingDto(string Key, string? Value);

/// <summary>
/// New raw value of the setting: "true" or "false" for boolean keys, a JSON array of rental asset
/// id strings for list keys.
/// </summary>
public sealed record PutTenantSettingRequestDto
{
    public required string Value { get; init; }
}
