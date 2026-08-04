START TRANSACTION;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM core.__ef_migrations_history WHERE "migration_id" = '20260804152356_AddTenantLogoSvg') THEN
    ALTER TABLE core.tenants ADD logo_svg text;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM core.__ef_migrations_history WHERE "migration_id" = '20260804152356_AddTenantLogoSvg') THEN
    INSERT INTO core.__ef_migrations_history (migration_id, product_version)
    VALUES ('20260804152356_AddTenantLogoSvg', '10.0.9');
    END IF;
END $EF$;
COMMIT;

START TRANSACTION;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM core.__ef_migrations_history WHERE "migration_id" = '20260804185540_AddRentalsScheduleAndLayouts') THEN
    ALTER TABLE rentals.rental_assets ADD allowed_duration_minutes character varying(128);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM core.__ef_migrations_history WHERE "migration_id" = '20260804185540_AddRentalsScheduleAndLayouts') THEN
    ALTER TABLE rentals.rental_assets ADD close_time time without time zone;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM core.__ef_migrations_history WHERE "migration_id" = '20260804185540_AddRentalsScheduleAndLayouts') THEN
    ALTER TABLE rentals.rental_assets ADD open_time time without time zone;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM core.__ef_migrations_history WHERE "migration_id" = '20260804185540_AddRentalsScheduleAndLayouts') THEN
    ALTER TABLE rentals.rental_assets ADD schedule_policy character varying(32) NOT NULL DEFAULT 'SlotGrid';
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM core.__ef_migrations_history WHERE "migration_id" = '20260804185540_AddRentalsScheduleAndLayouts') THEN
    CREATE TABLE rentals.layouts (
        id uuid NOT NULL,
        tenant_id uuid NOT NULL,
        unit_id uuid,
        name character varying(200) NOT NULL,
        is_active boolean NOT NULL DEFAULT TRUE,
        created_at timestamp with time zone NOT NULL,
        updated_at timestamp with time zone,
        CONSTRAINT pk_layouts PRIMARY KEY (id),
        CONSTRAINT fk_layouts_units_unit_id FOREIGN KEY (unit_id) REFERENCES core.units (id) ON DELETE SET NULL
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM core.__ef_migrations_history WHERE "migration_id" = '20260804185540_AddRentalsScheduleAndLayouts') THEN
    CREATE TABLE rentals.occupancy_kinds (
        id uuid NOT NULL,
        tenant_id uuid NOT NULL,
        key character varying(64) NOT NULL,
        label character varying(120) NOT NULL,
        color_hex character varying(16),
        is_bookable_by_customer boolean NOT NULL,
        blocks_capacity boolean NOT NULL,
        sort_order integer NOT NULL,
        is_active boolean NOT NULL DEFAULT TRUE,
        created_at timestamp with time zone NOT NULL,
        updated_at timestamp with time zone,
        CONSTRAINT pk_occupancy_kinds PRIMARY KEY (id)
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM core.__ef_migrations_history WHERE "migration_id" = '20260804185540_AddRentalsScheduleAndLayouts') THEN
    CREATE TABLE rentals.layout_items (
        id uuid NOT NULL,
        tenant_id uuid NOT NULL,
        layout_id uuid NOT NULL,
        rental_asset_id uuid NOT NULL,
        x_percent double precision NOT NULL,
        y_percent double precision NOT NULL,
        width_percent double precision NOT NULL,
        height_percent double precision NOT NULL,
        z_index integer NOT NULL,
        created_at timestamp with time zone NOT NULL,
        updated_at timestamp with time zone,
        CONSTRAINT pk_layout_items PRIMARY KEY (id),
        CONSTRAINT fk_layout_items_layouts_layout_id FOREIGN KEY (layout_id) REFERENCES rentals.layouts (id) ON DELETE CASCADE,
        CONSTRAINT fk_layout_items_rental_assets_rental_asset_id FOREIGN KEY (rental_asset_id) REFERENCES rentals.rental_assets (id) ON DELETE CASCADE
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM core.__ef_migrations_history WHERE "migration_id" = '20260804185540_AddRentalsScheduleAndLayouts') THEN
    CREATE TABLE rentals.schedule_templates (
        id uuid NOT NULL,
        tenant_id uuid NOT NULL,
        rental_asset_id uuid NOT NULL,
        day_of_week integer NOT NULL,
        start_time time without time zone NOT NULL,
        end_time time without time zone NOT NULL,
        occupancy_kind_id uuid NOT NULL,
        label character varying(200),
        is_active boolean NOT NULL DEFAULT TRUE,
        created_at timestamp with time zone NOT NULL,
        updated_at timestamp with time zone,
        CONSTRAINT pk_schedule_templates PRIMARY KEY (id),
        CONSTRAINT fk_schedule_templates_occupancy_kinds_occupancy_kind_id FOREIGN KEY (occupancy_kind_id) REFERENCES rentals.occupancy_kinds (id) ON DELETE RESTRICT,
        CONSTRAINT fk_schedule_templates_rental_assets_rental_asset_id FOREIGN KEY (rental_asset_id) REFERENCES rentals.rental_assets (id) ON DELETE CASCADE
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM core.__ef_migrations_history WHERE "migration_id" = '20260804185540_AddRentalsScheduleAndLayouts') THEN
    CREATE TABLE rentals.slots (
        id uuid NOT NULL,
        tenant_id uuid NOT NULL,
        rental_asset_id uuid NOT NULL,
        date date NOT NULL,
        start_time time without time zone NOT NULL,
        end_time time without time zone NOT NULL,
        occupancy_kind_id uuid NOT NULL,
        label character varying(200),
        status character varying(32) NOT NULL DEFAULT 'Available',
        reservation_id uuid,
        source_template_id uuid,
        created_at timestamp with time zone NOT NULL,
        updated_at timestamp with time zone,
        CONSTRAINT pk_slots PRIMARY KEY (id),
        CONSTRAINT fk_slots_occupancy_kinds_occupancy_kind_id FOREIGN KEY (occupancy_kind_id) REFERENCES rentals.occupancy_kinds (id) ON DELETE RESTRICT,
        CONSTRAINT fk_slots_rental_assets_rental_asset_id FOREIGN KEY (rental_asset_id) REFERENCES rentals.rental_assets (id) ON DELETE CASCADE,
        CONSTRAINT fk_slots_reservations_reservation_id FOREIGN KEY (reservation_id) REFERENCES rentals.reservations (id) ON DELETE SET NULL,
        CONSTRAINT fk_slots_schedule_templates_source_template_id FOREIGN KEY (source_template_id) REFERENCES rentals.schedule_templates (id) ON DELETE SET NULL
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM core.__ef_migrations_history WHERE "migration_id" = '20260804185540_AddRentalsScheduleAndLayouts') THEN
    CREATE UNIQUE INDEX ix_layout_items_layout_id_rental_asset_id ON rentals.layout_items (layout_id, rental_asset_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM core.__ef_migrations_history WHERE "migration_id" = '20260804185540_AddRentalsScheduleAndLayouts') THEN
    CREATE INDEX ix_layout_items_rental_asset_id ON rentals.layout_items (rental_asset_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM core.__ef_migrations_history WHERE "migration_id" = '20260804185540_AddRentalsScheduleAndLayouts') THEN
    CREATE INDEX ix_layouts_tenant_id_is_active ON rentals.layouts (tenant_id, is_active);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM core.__ef_migrations_history WHERE "migration_id" = '20260804185540_AddRentalsScheduleAndLayouts') THEN
    CREATE INDEX ix_layouts_unit_id ON rentals.layouts (unit_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM core.__ef_migrations_history WHERE "migration_id" = '20260804185540_AddRentalsScheduleAndLayouts') THEN
    CREATE INDEX ix_occupancy_kinds_tenant_id_is_active_sort_order ON rentals.occupancy_kinds (tenant_id, is_active, sort_order);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM core.__ef_migrations_history WHERE "migration_id" = '20260804185540_AddRentalsScheduleAndLayouts') THEN
    CREATE UNIQUE INDEX ix_occupancy_kinds_tenant_id_key ON rentals.occupancy_kinds (tenant_id, key);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM core.__ef_migrations_history WHERE "migration_id" = '20260804185540_AddRentalsScheduleAndLayouts') THEN
    CREATE INDEX ix_schedule_templates_occupancy_kind_id ON rentals.schedule_templates (occupancy_kind_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM core.__ef_migrations_history WHERE "migration_id" = '20260804185540_AddRentalsScheduleAndLayouts') THEN
    CREATE INDEX ix_schedule_templates_rental_asset_id ON rentals.schedule_templates (rental_asset_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM core.__ef_migrations_history WHERE "migration_id" = '20260804185540_AddRentalsScheduleAndLayouts') THEN
    CREATE INDEX ix_schedule_templates_tenant_id_rental_asset_id_day_of_week_st ON rentals.schedule_templates (tenant_id, rental_asset_id, day_of_week, start_time);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM core.__ef_migrations_history WHERE "migration_id" = '20260804185540_AddRentalsScheduleAndLayouts') THEN
    CREATE INDEX ix_slots_occupancy_kind_id ON rentals.slots (occupancy_kind_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM core.__ef_migrations_history WHERE "migration_id" = '20260804185540_AddRentalsScheduleAndLayouts') THEN
    CREATE INDEX ix_slots_rental_asset_id ON rentals.slots (rental_asset_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM core.__ef_migrations_history WHERE "migration_id" = '20260804185540_AddRentalsScheduleAndLayouts') THEN
    CREATE INDEX ix_slots_reservation_id ON rentals.slots (reservation_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM core.__ef_migrations_history WHERE "migration_id" = '20260804185540_AddRentalsScheduleAndLayouts') THEN
    CREATE INDEX ix_slots_source_template_id ON rentals.slots (source_template_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM core.__ef_migrations_history WHERE "migration_id" = '20260804185540_AddRentalsScheduleAndLayouts') THEN
    CREATE INDEX ix_slots_tenant_id_date_status ON rentals.slots (tenant_id, date, status);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM core.__ef_migrations_history WHERE "migration_id" = '20260804185540_AddRentalsScheduleAndLayouts') THEN
    CREATE UNIQUE INDEX ix_slots_tenant_id_rental_asset_id_date_start_time ON rentals.slots (tenant_id, rental_asset_id, date, start_time);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM core.__ef_migrations_history WHERE "migration_id" = '20260804185540_AddRentalsScheduleAndLayouts') THEN
    INSERT INTO core.__ef_migrations_history (migration_id, product_version)
    VALUES ('20260804185540_AddRentalsScheduleAndLayouts', '10.0.9');
    END IF;
END $EF$;
COMMIT;

