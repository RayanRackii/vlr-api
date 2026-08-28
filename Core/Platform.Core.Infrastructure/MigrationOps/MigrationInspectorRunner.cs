using Microsoft.EntityFrameworkCore;
using Platform.Core.Infrastructure.Persistence;

namespace Platform.Core.Infrastructure.MigrationOps;

public static class MigrationInspectorRunner
{
    public const string ProductionApplyConfirmation = "APPLY_PRODUCTION";

    public static async Task RunAsync(
        MigrationInspectorRequest request,
        TextWriter output,
        Func<string, AppDbContext>? dbContextFactory = null,
        CancellationToken cancellationToken = default)
    {
        var target = DatabaseTargets.NormalizeTarget(request.Target);
        var mode = DatabaseTargets.NormalizeMode(request.Mode);

        if (target == DatabaseTargets.Production
            && mode == "apply"
            && request.ConfirmProduction != ProductionApplyConfirmation)
        {
            throw new DatabaseTargetException(
                DatabaseTargetCodes.ProductionConfirmationRequired,
                "FAIL CLOSED: production apply requires confirm_production=APPLY_PRODUCTION.");
        }

        var identity = DatabaseTargetGuard.Validate(target, request.ConnectionString);
        WriteIdentity(output, identity);

        var connectionString = request.ConnectionString!;
        var factory = dbContextFactory ?? MigrationDbContextFactory.Create;
        await using var dbContext = factory(connectionString);

        var assemblyMigrations = dbContext.Database.GetMigrations().ToList();
        var appliedMigrations = (await dbContext.Database.GetAppliedMigrationsAsync(cancellationToken))
            .ToList();
        var pendingMigrations = MigrationInventory.ComputePending(assemblyMigrations, appliedMigrations);

        WriteInventory(
            output,
            assemblyMigrations,
            appliedMigrations,
            pendingMigrations);

        if (mode == "list")
        {
            var diagnostics = await RbacSchemaDiagnostics.CollectAsync(dbContext, cancellationToken);
            WriteDiagnostics(output, diagnostics);
            return;
        }

        output.WriteLine($"APPLYING {pendingMigrations.Count} pending migration(s).");
        await dbContext.Database.MigrateAsync(cancellationToken);

        var appliedAfter = (await dbContext.Database.GetAppliedMigrationsAsync(cancellationToken))
            .ToList();
        var pendingAfter = MigrationInventory.ComputePending(assemblyMigrations, appliedAfter);
        if (pendingAfter.Count > 0)
        {
            output.WriteLine("STILL_PENDING");
            foreach (var migration in pendingAfter)
            {
                output.WriteLine($"  {migration}");
            }

            throw new DatabaseTargetException(
                DatabaseTargetCodes.PostcheckFailed,
                $"{DatabaseTargetCodes.PostcheckFailed}: {pendingAfter.Count} assembly migration(s) still pending.");
        }

        output.WriteLine("MIGRATION_POSTCHECK=PASS");
        output.WriteLine($"CURRENT_LAST_MIGRATION={MigrationInventory.LastApplied(appliedAfter) ?? "(none)"}");
    }

    private static void WriteIdentity(TextWriter output, DatabaseTargetIdentity identity)
    {
        output.WriteLine($"TARGET={identity.Target}");
        output.WriteLine($"EXPECTED_REF={identity.ExpectedRef}");
        output.WriteLine($"DETECTED_REF={identity.DetectedRef}");
        output.WriteLine($"IDENTITY={(identity.IsSafe ? "SAFE" : "UNSAFE")}");
    }

    private static void WriteInventory(
        TextWriter output,
        IReadOnlyList<string> assemblyMigrations,
        IReadOnlyList<string> appliedMigrations,
        IReadOnlyList<string> pendingMigrations)
    {
        output.WriteLine($"EF_HISTORY={MigrationDbContextFactory.HistorySchema}.{MigrationDbContextFactory.HistoryTable}");
        output.WriteLine($"ASSEMBLY_MIGRATIONS={assemblyMigrations.Count}");
        output.WriteLine($"APPLIED_COUNT={appliedMigrations.Count}");
        output.WriteLine($"PENDING_COUNT={pendingMigrations.Count}");
        output.WriteLine($"CURRENT_LAST_MIGRATION={MigrationInventory.LastApplied(appliedMigrations) ?? "(none)"}");

        output.WriteLine("APPLIED");
        foreach (var migration in appliedMigrations)
        {
            output.WriteLine($"  {migration}");
        }

        output.WriteLine("PENDING");
        if (pendingMigrations.Count == 0)
        {
            output.WriteLine("  (none)");
        }
        else
        {
            foreach (var migration in pendingMigrations)
            {
                output.WriteLine($"  {migration}");
            }
        }
    }

    private static void WriteDiagnostics(TextWriter output, RbacSchemaDiagnosticCounts diagnostics)
    {
        output.WriteLine($"CLIENT_ASSIGNMENTS={diagnostics.ClientAssignments}");
        if (diagnostics.ClientAssignments > 0)
        {
            output.WriteLine("CLIENT_ROLE_USAGE_FOUND");
        }

        output.WriteLine($"SUPERADMIN_ASSIGNMENTS={diagnostics.SuperAdminAssignments}");
        output.WriteLine($"TECHNICIAN_ASSIGNMENTS={diagnostics.TechnicianAssignments}");
        output.WriteLine($"DUPLICATE_ROLE_NAME_GROUPS={diagnostics.DuplicateRoleNameGroups}");
        output.WriteLine($"ORPHAN_USER_ROLES={diagnostics.OrphanUserRoles}");
        output.WriteLine($"ORPHAN_ROLE_PERMISSIONS={diagnostics.OrphanRolePermissions}");
    }
}
