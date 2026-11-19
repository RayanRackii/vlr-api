namespace Platform.Api.Modules.Company.Dtos;

/// <summary>Customer portal login appearance of the current tenant (image + texts).</summary>
public sealed record PortalAppearanceResponse(
    string? LandingImageUrl,
    string? LandingKicker,
    string? LandingHeadline,
    string? LandingSubtitle,
    string? WelcomeTagline);

/// <summary>Full replace of the three landing texts: absent or blank clears the text.</summary>
public sealed record UpdatePortalAppearanceRequest
{
    public string? LandingKicker { get; init; }

    public string? LandingHeadline { get; init; }

    public string? LandingSubtitle { get; init; }
}
