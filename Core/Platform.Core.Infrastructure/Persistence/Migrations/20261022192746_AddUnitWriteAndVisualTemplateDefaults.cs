using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;
using Platform.Core.Domain.Entities;

#nullable disable

namespace Platform.Core.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddUnitWriteAndVisualTemplateDefaults : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "default_allowed_duration_minutes",
                schema: "core",
                table: "resource_visual_template_revisions",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<TimeOnly>(
                name: "default_close_time",
                schema: "core",
                table: "resource_visual_template_revisions",
                type: "time without time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "default_max_advance_booking_days",
                schema: "core",
                table: "resource_visual_template_revisions",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<TimeOnly>(
                name: "default_open_time",
                schema: "core",
                table: "resource_visual_template_revisions",
                type: "time without time zone",
                nullable: true);

            migrationBuilder.AddColumn<List<ResourceVisualDefaultPricing>>(
                name: "default_pricings",
                schema: "core",
                table: "resource_visual_template_revisions",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "default_requires_deposit",
                schema: "core",
                table: "resource_visual_template_revisions",
                type: "boolean",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "default_schedule_policy",
                schema: "core",
                table: "resource_visual_template_revisions",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.Sql("""
                INSERT INTO core.permissions (id, key, name, description, module_key, created_at)
                VALUES (gen_random_uuid(), 'core.units.write', 'Write units', 'Create and update tenant units.', NULL, now())
                ON CONFLICT (key) DO NOTHING;

                INSERT INTO core.role_permissions (role_id, permission_id, granted_at)
                SELECT r.id, p.id, now()
                FROM core.roles r CROSS JOIN core.permissions p
                WHERE r.is_system_role
                  AND lower(r.name) IN ('admin', 'superadmin')
                  AND p.key = 'core.units.write'
                ON CONFLICT DO NOTHING;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "default_allowed_duration_minutes",
                schema: "core",
                table: "resource_visual_template_revisions");

            migrationBuilder.DropColumn(
                name: "default_close_time",
                schema: "core",
                table: "resource_visual_template_revisions");

            migrationBuilder.DropColumn(
                name: "default_max_advance_booking_days",
                schema: "core",
                table: "resource_visual_template_revisions");

            migrationBuilder.DropColumn(
                name: "default_open_time",
                schema: "core",
                table: "resource_visual_template_revisions");

            migrationBuilder.DropColumn(
                name: "default_pricings",
                schema: "core",
                table: "resource_visual_template_revisions");

            migrationBuilder.DropColumn(
                name: "default_requires_deposit",
                schema: "core",
                table: "resource_visual_template_revisions");

            migrationBuilder.DropColumn(
                name: "default_schedule_policy",
                schema: "core",
                table: "resource_visual_template_revisions");

            migrationBuilder.Sql("""
                DELETE FROM core.role_permissions rp
                USING core.roles r, core.permissions p
                WHERE rp.role_id = r.id
                  AND rp.permission_id = p.id
                  AND r.is_system_role
                  AND lower(r.name) IN ('admin', 'superadmin')
                  AND p.key = 'core.units.write';
                DELETE FROM core.permissions WHERE key = 'core.units.write';
                """);
        }
    }
}
