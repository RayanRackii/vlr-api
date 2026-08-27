namespace Platform.Core.Infrastructure.MigrationOps;

public sealed record DatabaseTargetIdentity(
    string Target,
    string ExpectedRef,
    string DetectedRef)
{
    public bool IsSafe =>
        string.Equals(ExpectedRef, DetectedRef, StringComparison.Ordinal);
}
