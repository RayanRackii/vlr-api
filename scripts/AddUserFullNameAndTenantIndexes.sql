START TRANSACTION;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM core.__ef_migrations_history WHERE "migration_id" = '20260805171718_AddUserFullNameAndTenantIndexes') THEN
    CREATE INDEX IF NOT EXISTS ix_users_full_name ON core.users (full_name);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM core.__ef_migrations_history WHERE "migration_id" = '20260805171718_AddUserFullNameAndTenantIndexes') THEN
    CREATE INDEX IF NOT EXISTS ix_users_tenant_id ON core.users (tenant_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM core.__ef_migrations_history WHERE "migration_id" = '20260805171718_AddUserFullNameAndTenantIndexes') THEN
    INSERT INTO core.__ef_migrations_history (migration_id, product_version)
    VALUES ('20260805171718_AddUserFullNameAndTenantIndexes', '10.0.9');
    END IF;
END $EF$;

COMMIT;
