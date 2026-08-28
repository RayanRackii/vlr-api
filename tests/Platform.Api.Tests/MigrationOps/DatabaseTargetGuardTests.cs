using Platform.Core.Infrastructure.MigrationOps;

namespace Platform.Api.Tests.MigrationOps;

public sealed class DatabaseTargetGuardTests
{
    private const string FakePassword = "test-password-token-not-real";

    [Fact]
    public void Development_with_dev_pooler_username_is_safe()
    {
        var identity = DatabaseTargetGuard.Validate(
            DatabaseTargets.Development,
            Pooler("postgres." + DatabaseTargets.DevelopmentSupabaseRef));

        Assert.Equal(DatabaseTargets.Development, identity.Target);
        Assert.Equal(DatabaseTargets.DevelopmentSupabaseRef, identity.DetectedRef);
        Assert.True(identity.IsSafe);
        Assert.DoesNotContain(FakePassword, identity.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Production_with_prod_pooler_username_is_safe()
    {
        var identity = DatabaseTargetGuard.Validate(
            DatabaseTargets.Production,
            Pooler("postgres." + DatabaseTargets.ProductionSupabaseRef));

        Assert.Equal(DatabaseTargets.ProductionSupabaseRef, identity.DetectedRef);
        Assert.True(identity.IsSafe);
    }

    [Fact]
    public void Development_with_prod_ref_is_rejected()
    {
        var ex = Assert.Throws<DatabaseTargetException>(() =>
            DatabaseTargetGuard.Validate(
                DatabaseTargets.Development,
                Pooler("postgres." + DatabaseTargets.ProductionSupabaseRef)));

        Assert.Equal(DatabaseTargetCodes.Mismatch, ex.Code);
        Assert.Contains(DatabaseTargets.ProductionSupabaseRef, ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(FakePassword, ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("Password=", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Production_with_dev_ref_is_rejected()
    {
        var ex = Assert.Throws<DatabaseTargetException>(() =>
            DatabaseTargetGuard.Validate(
                DatabaseTargets.Production,
                Pooler("postgres." + DatabaseTargets.DevelopmentSupabaseRef)));

        Assert.Equal(DatabaseTargetCodes.Mismatch, ex.Code);
        Assert.DoesNotContain(FakePassword, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Direct_host_db_projectref_supabase_co_is_accepted()
    {
        var connection =
            $"Host=db.{DatabaseTargets.DevelopmentSupabaseRef}.supabase.co;Database=postgres;Username=postgres;Password={FakePassword}";

        var detected = DatabaseTargetGuard.ExtractSupabaseProjectRef(connection);

        Assert.Equal(DatabaseTargets.DevelopmentSupabaseRef, detected);
    }

    [Fact]
    public void Missing_connection_string_is_rejected()
    {
        var ex = Assert.Throws<DatabaseTargetException>(() =>
            DatabaseTargetGuard.Validate(DatabaseTargets.Development, "  "));

        Assert.Equal(DatabaseTargetCodes.ConnectionMissing, ex.Code);
    }

    [Fact]
    public void Null_connection_string_is_rejected()
    {
        var ex = Assert.Throws<DatabaseTargetException>(() =>
            DatabaseTargetGuard.Validate(DatabaseTargets.Development, null));

        Assert.Equal(DatabaseTargetCodes.ConnectionMissing, ex.Code);
    }

    [Fact]
    public void Malformed_connection_string_is_rejected()
    {
        var ex = Assert.Throws<DatabaseTargetException>(() =>
            DatabaseTargetGuard.ExtractSupabaseProjectRef("definitely-not-a-connection-string"));

        Assert.Equal(DatabaseTargetCodes.ConnectionMalformed, ex.Code);
        Assert.DoesNotContain("definitely-not-a-connection-string", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Username_without_project_ref_is_rejected()
    {
        var ex = Assert.Throws<DatabaseTargetException>(() =>
            DatabaseTargetGuard.ExtractSupabaseProjectRef(
                Pooler("postgres")));

        Assert.Equal(DatabaseTargetCodes.ConnectionMalformed, ex.Code);
        Assert.DoesNotContain(FakePassword, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Empty_target_is_rejected()
    {
        var ex = Assert.Throws<DatabaseTargetException>(() =>
            DatabaseTargets.NormalizeTarget(""));

        Assert.Equal(DatabaseTargetCodes.TargetInvalid, ex.Code);
    }

    private static string Pooler(string username) =>
        "Host=aws-0-sa-east-1.pooler.supabase.com;"
        + "Port=6543;"
        + "Database=postgres;"
        + $"Username={username};"
        + $"Password={FakePassword};"
        + "SSL Mode=Require";
}
