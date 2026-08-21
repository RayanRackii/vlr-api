namespace Platform.Api.Tests.Infrastructure;

/// <summary>
/// Skips the test when the Docker named pipe (Windows) or socket (Unix) is missing.
/// Does not fail the suite; concurrency tests need a real PostgreSQL container.
/// </summary>
public sealed class DockerFactAttribute : FactAttribute
{
    public DockerFactAttribute()
    {
        if (!DockerEnvironment.IsAvailable)
        {
            Skip = "Docker is not available; concurrency tests were skipped.";
        }
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
