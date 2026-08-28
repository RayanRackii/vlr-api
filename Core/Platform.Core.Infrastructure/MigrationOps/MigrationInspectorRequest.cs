namespace Platform.Core.Infrastructure.MigrationOps;

public sealed record MigrationInspectorRequest(
    string? Target,
    string? Mode,
    string? ConnectionString,
    string? ConfirmProduction);
