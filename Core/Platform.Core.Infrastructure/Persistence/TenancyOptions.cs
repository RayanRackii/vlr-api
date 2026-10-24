namespace Platform.Core.Infrastructure.Persistence;

/// <summary>Passo 7 kill switch for the global tenant filter.</summary>
public sealed class TenancyOptions
{
    public const string SectionName = "Tenancy";

    /// <summary>
    /// false (default): null tenant disables the filter (legacy fail-open).
    /// true: null tenant reads zero rows unless a <see cref="PlatformScope"/> is active.
    /// </summary>
    public bool FailClosed { get; set; }
}
