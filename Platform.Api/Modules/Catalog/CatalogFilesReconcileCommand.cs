using Platform.Api.Modules.Catalog.Services;
using Platform.Api.Storage;
using Platform.Core.Infrastructure;
using Platform.Core.Infrastructure.Persistence;

namespace Platform.Api.Modules.Catalog;

public static class CatalogFilesReconcileCommand
{
    public static async Task RunAsync(string[] args)
    {
        if (args.Any(arg => arg != "--apply"))
            throw new ArgumentException("Usage: dotnet Platform.Api.dll catalog-files-reconcile [--apply]");

        // Local operator entry point only: requires deployment/admin DB + storage credentials.
        // No web listener, HTTP route, Hangfire, migrations, seeding or notification workers.
        var builder = Host.CreateApplicationBuilder();
        var connectionString = builder.Configuration.GetConnectionString("DefaultConnection")
            ?? throw new InvalidOperationException("Connection string 'DefaultConnection' is not configured.");
        builder.Services.AddSingleton<ITenantProvider, OperatorTenantProvider>();
        builder.Services.AddCorePersistence(connectionString);
        builder.Services.AddStorage(builder.Configuration, builder.Environment);
        builder.Services.AddScoped<CatalogFilesReconciler>();
        using var host = builder.Build();
        await using var scope = host.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<CatalogFilesReconciler>()
            .RunAsync(Console.Out, apply: args.Contains("--apply", StringComparer.Ordinal));
    }

    private sealed class OperatorTenantProvider : ITenantProvider
    {
        public Guid? TenantId => null;
    }
}
