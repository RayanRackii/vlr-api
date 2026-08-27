using Platform.Core.Infrastructure.MigrationOps;

var request = new MigrationInspectorRequest(
    Target: Environment.GetEnvironmentVariable("MIGRATION_TARGET"),
    Mode: Environment.GetEnvironmentVariable("MIGRATION_MODE"),
    ConnectionString: Environment.GetEnvironmentVariable("ConnectionStrings__DefaultConnection"),
    ConfirmProduction: Environment.GetEnvironmentVariable("CONFIRM_PRODUCTION"));

try
{
    await MigrationInspectorRunner.RunAsync(request, Console.Out);
    return 0;
}
catch (DatabaseTargetException ex)
{
    Console.Error.WriteLine($"CODE={ex.Code}");
    Console.Error.WriteLine(SecretLogSanitizer.Sanitize(ex.Message, request.ConnectionString));
    return 1;
}
catch (Exception ex)
{
    Console.Error.WriteLine("CODE=MIGRATION_INSPECTOR_FAILED");
    Console.Error.WriteLine(SecretLogSanitizer.Sanitize($"{ex.GetType().Name}: {ex.Message}", request.ConnectionString));
    return 1;
}
