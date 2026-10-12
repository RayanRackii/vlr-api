namespace Platform.Api.Tests.Infrastructure;

/// <summary>
/// Skips the test when the Docker named pipe (Windows) or socket (Unix) is missing.
/// Does not fail the suite; concurrency tests need a real PostgreSQL container.
/// </summary>
public sealed class DockerFactAttribute : FactAttribute
{
    public DockerFactAttribute()
    {
        if (!DockerEnvironment.IsAvailable && !LocalPostgresEnvironment.IsConfigured)
        {
            Skip = "Docker and an isolated local PostgreSQL test connection are not available; concurrency tests were skipped.";
        }
    }
}

internal static class LocalPostgresEnvironment
{
    public const string ConnectionStringVariable = "ROLVIX_TEST_POSTGRES_CONNECTION";

    public static bool IsConfigured =>
        !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(ConnectionStringVariable));

    public static Npgsql.NpgsqlConnectionStringBuilder CreateLoopbackBuilder()
    {
        var value = Environment.GetEnvironmentVariable(ConnectionStringVariable);
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException("Local PostgreSQL test connection is not configured.");
        }

        var builder = new Npgsql.NpgsqlConnectionStringBuilder(value);
        if (builder.Host is not ("127.0.0.1" or "localhost" or "::1"))
        {
            throw new InvalidOperationException(
                "Local PostgreSQL test connection must use a loopback host.");
        }

        return builder;
    }
}

internal static class DockerEnvironment
{
    public static bool IsAvailable { get; } = Detect();

    private static bool Detect()
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                return NamedPipeExists("docker_engine")
                    || NamedPipeExists("dockerDesktopLinuxEngine")
                    || NamedPipeExists("docker_wsl");
            }

            return File.Exists("/var/run/docker.sock")
                || File.Exists("/run/docker.sock")
                || File.Exists("/var/run/podman/podman.sock");
        }
        catch
        {
            return false;
        }
    }

    private static bool NamedPipeExists(string pipeName)
    {
        try
        {
            return File.Exists($@"\\.\pipe\{pipeName}");
        }
        catch
        {
            return false;
        }
    }
}
