namespace Platform.Core.Infrastructure.MigrationOps;

public sealed record RbacSchemaDiagnosticCounts(
    int ClientAssignments,
    int SuperAdminAssignments,
    int TechnicianAssignments,
    int DuplicateRoleNameGroups,
    int OrphanUserRoles,
    int OrphanRolePermissions);
