namespace Platform.Api.Authentication;

public sealed class PlatformAdminOptions
{
    public const string SectionName = "PlatformAdmin";

    /// <summary>
    /// Emails allowed to manage platform-wide tenant provisioning (Super-Admin).
    /// </summary>
    public string[] Emails { get; set; } = [];
}
