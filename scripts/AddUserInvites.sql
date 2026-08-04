START TRANSACTION;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM core.__ef_migrations_history WHERE "migration_id" = '20260804191447_AddUserInvites') THEN
    CREATE TABLE core.user_invites (
        id uuid NOT NULL,
        tenant_id uuid NOT NULL,
        email character varying(320) NOT NULL,
        full_name character varying(200) NOT NULL,
        role_name character varying(64) NOT NULL,
        token character varying(64) NOT NULL,
        expires_at timestamp with time zone NOT NULL,
        accepted_at timestamp with time zone,
        revoked_at timestamp with time zone,
        created_user_id uuid,
        created_at timestamp with time zone NOT NULL,
        updated_at timestamp with time zone,
        CONSTRAINT pk_user_invites PRIMARY KEY (id),
        CONSTRAINT fk_user_invites_tenants_tenant_id FOREIGN KEY (tenant_id) REFERENCES core.tenants (id) ON DELETE CASCADE
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM core.__ef_migrations_history WHERE "migration_id" = '20260804191447_AddUserInvites') THEN
    CREATE INDEX ix_user_invites_tenant_id_email_accepted_at ON core.user_invites (tenant_id, email, accepted_at);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM core.__ef_migrations_history WHERE "migration_id" = '20260804191447_AddUserInvites') THEN
    CREATE UNIQUE INDEX ix_user_invites_token ON core.user_invites (token);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM core.__ef_migrations_history WHERE "migration_id" = '20260804191447_AddUserInvites') THEN
    INSERT INTO core.__ef_migrations_history (migration_id, product_version)
    VALUES ('20260804191447_AddUserInvites', '10.0.9');
    END IF;
END $EF$;
COMMIT;

