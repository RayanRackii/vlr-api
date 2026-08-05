START TRANSACTION;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM core.__ef_migrations_history WHERE "migration_id" = '20260805185352_UserMembershipPerTenant') THEN
    DROP INDEX IF EXISTS core.ix_users_supabase_auth_id;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM core.__ef_migrations_history WHERE "migration_id" = '20260805185352_UserMembershipPerTenant') THEN
    CREATE UNIQUE INDEX IF NOT EXISTS ix_users_tenant_id_supabase_auth_id
        ON core.users (tenant_id, supabase_auth_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM core.__ef_migrations_history WHERE "migration_id" = '20260805185352_UserMembershipPerTenant') THEN
    INSERT INTO core.__ef_migrations_history (migration_id, product_version)
    VALUES ('20260805185352_UserMembershipPerTenant', '10.0.9');
    END IF;
END $EF$;

COMMIT;
