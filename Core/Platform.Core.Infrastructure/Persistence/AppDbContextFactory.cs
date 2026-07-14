using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.Configuration;

namespace Platform.Core.Infrastructure.Persistence;

public sealed class AppDbContextFactory : IDesignTimeDbContextFactory<AppDbContext>
{
    public AppDbContext CreateDbContext(string[] args)
    {
        var configuration = new ConfigurationBuilder()
            .SetBasePath(FindApiProjectDirectory())
            .AddJsonFile("appsettings.json", optional: false)
            .AddJsonFile("appsettings.Development.json", optional: true)
            .AddEnvironmentVariables()
            .Build();

        var connectionString = configuration.GetConnectionString("DefaultConnection")
            ?? throw new InvalidOperationException(
                "Connection string 'DefaultConnection' was not found.");

        var optionsBuilder = new DbContextOptionsBuilder<AppDbContext>();

        optionsBuilder
            .UseNpgsql(
                connectionString,
                npgsql => npgsql.MigrationsHistoryTable("__ef_migrations_history", "core"))
            .UseSnakeCaseNamingConvention();

        return new AppDbContext(optionsBuilder.Options, new NullTenantProvider());
    }

    private static string FindApiProjectDirectory()
    {
        var directory = new DirectoryInfo(Directory.GetCurrentDirectory());

        while (directory is not null)
        {
            var apiProjectPath = Path.Combine(directory.FullName, "Platform.Api");
            if (File.Exists(Path.Combine(apiProjectPath, "appsettings.json")))
            {
                return apiProjectPath;
            }

            if (File.Exists(Path.Combine(directory.FullName, "appsettings.json")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException(
            "Could not locate Platform.Api/appsettings.json for design-time DbContext creation.");
    }
}
