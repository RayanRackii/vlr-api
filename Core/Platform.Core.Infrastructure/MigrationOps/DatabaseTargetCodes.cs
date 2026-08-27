namespace Platform.Core.Infrastructure.MigrationOps;

public static class DatabaseTargetCodes
{
    public const string Mismatch = "DATABASE_TARGET_MISMATCH";

    public const string ConnectionMissing = "DATABASE_CONNECTION_MISSING";

    public const string ConnectionMalformed = "DATABASE_CONNECTION_MALFORMED";

    public const string TargetInvalid = "DATABASE_TARGET_INVALID";

    public const string ModeInvalid = "MIGRATION_MODE_INVALID";

    public const string ProductionConfirmationRequired = "PRODUCTION_CONFIRMATION_REQUIRED";

    public const string PostcheckFailed = "MIGRATION_POSTCHECK_FAILED";
}
