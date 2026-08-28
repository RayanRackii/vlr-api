using Platform.Core.Infrastructure.MigrationOps;

namespace Platform.Api.Tests.MigrationOps;

public sealed class MigrationInspectorRunnerTests
{
    private const string FakePassword = "test-password-token-not-real";

    [Fact]
    public async Task Production_apply_without_confirmation_fails_closed_before_database()
    {
        var factoryCalled = false;
        await using var writer = new StringWriter();

        var ex = await Assert.ThrowsAsync<DatabaseTargetException>(() =>
            MigrationInspectorRunner.RunAsync(
                new MigrationInspectorRequest(
                    DatabaseTargets.Production,
                    "apply",
                    Pooler(DatabaseTargets.ProductionSupabaseRef),
                    ConfirmProduction: "nope"),
                writer,
                _ =>
                {
                    factoryCalled = true;
                    throw new InvalidOperationException("database must not be opened");
                }));

        Assert.Equal(DatabaseTargetCodes.ProductionConfirmationRequired, ex.Code);
        Assert.False(factoryCalled);
        Assert.DoesNotContain(FakePassword, writer.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Mismatch_fails_before_database()
    {
        var factoryCalled = false;
        await using var writer = new StringWriter();

        var ex = await Assert.ThrowsAsync<DatabaseTargetException>(() =>
            MigrationInspectorRunner.RunAsync(
                new MigrationInspectorRequest(
                    DatabaseTargets.Development,
                    "list",
                    Pooler(DatabaseTargets.ProductionSupabaseRef),
                    ConfirmProduction: null),
                writer,
                _ =>
                {
                    factoryCalled = true;
                    throw new InvalidOperationException("database must not be opened");
                }));

        Assert.Equal(DatabaseTargetCodes.Mismatch, ex.Code);
        Assert.False(factoryCalled);
        Assert.DoesNotContain(FakePassword, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Sanitizer_redacts_password_and_full_connection_string()
    {
        var connection = Pooler(DatabaseTargets.DevelopmentSupabaseRef);
        var leaked = $"failed: {connection}";

        var sanitized = SecretLogSanitizer.Sanitize(leaked, connection);

        Assert.DoesNotContain(FakePassword, sanitized, StringComparison.Ordinal);
        Assert.DoesNotContain(connection, sanitized, StringComparison.Ordinal);
        Assert.Contains("[redacted]", sanitized, StringComparison.Ordinal);
    }

    private static string Pooler(string projectRef) =>
        "Host=aws-0-sa-east-1.pooler.supabase.com;Database=postgres;"
        + $"Username=postgres.{projectRef};Password={FakePassword};SSL Mode=Require";
}
