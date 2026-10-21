using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Platform.Core.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddResourceVisualCatalog : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "resource_visual_tags",
                schema: "core",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    slug = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    kind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    facet = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    name_i18n = table.Column<Dictionary<string, string>>(type: "jsonb", nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    sort_order = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_resource_visual_tags", x => x.id);
                    table.UniqueConstraint("ak_resource_visual_tags_id_kind", x => new { x.id, x.kind });
                });

            migrationBuilder.CreateTable(
                name: "resource_visual_templates",
                schema: "core",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    slug = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    name_i18n = table.Column<Dictionary<string, string>>(type: "jsonb", nullable: false),
                    description_i18n = table.Column<Dictionary<string, string>>(type: "jsonb", nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    sort_order = table.Column<int>(type: "integer", nullable: false),
                    version = table.Column<long>(type: "bigint", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_resource_visual_templates", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "tenant_resource_segments",
                schema: "core",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    tag_id = table.Column<Guid>(type: "uuid", nullable: false),
                    tag_kind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_tenant_resource_segments", x => new { x.tenant_id, x.tag_id });
                    table.CheckConstraint("ck_tenant_resource_segments_segment", "tag_kind = 'Segment'");
                    table.ForeignKey(
                        name: "fk_tenant_resource_segments_resource_visual_tags_tag_id_tag_ki",
                        columns: x => new { x.tag_id, x.tag_kind },
                        principalSchema: "core",
                        principalTable: "resource_visual_tags",
                        principalColumns: new[] { "id", "kind" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_tenant_resource_segments_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalSchema: "core",
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "resource_visual_template_revisions",
                schema: "core",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    template_id = table.Column<Guid>(type: "uuid", nullable: false),
                    revision_no = table.Column<int>(type: "integer", nullable: false),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    floor_svg = table.Column<string>(type: "text", nullable: false),
                    lines_svg = table.Column<string>(type: "text", nullable: true),
                    net_svg = table.Column<string>(type: "text", nullable: true),
                    view_box = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    aspect_ratio = table.Column<decimal>(type: "numeric(12,6)", precision: 12, scale: 6, nullable: false),
                    default_color = table.Column<string>(type: "character varying(7)", maxLength: 7, nullable: false),
                    default_icon = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    suggested_slot_minutes = table.Column<int>(type: "integer", nullable: true),
                    suggested_capacity_people = table.Column<int>(type: "integer", nullable: true),
                    art_sha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    published_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    version = table.Column<long>(type: "bigint", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_resource_visual_template_revisions", x => x.id);
                    table.ForeignKey(
                        name: "fk_resource_visual_template_revisions_resource_visual_template",
                        column: x => x.template_id,
                        principalSchema: "core",
                        principalTable: "resource_visual_templates",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "resource_visual_template_tags",
                schema: "core",
                columns: table => new
                {
                    template_id = table.Column<Guid>(type: "uuid", nullable: false),
                    tag_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_resource_visual_template_tags", x => new { x.template_id, x.tag_id });
                    table.ForeignKey(
                        name: "fk_resource_visual_template_tags_resource_visual_tags_tag_id",
                        column: x => x.tag_id,
                        principalSchema: "core",
                        principalTable: "resource_visual_tags",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_resource_visual_template_tags_resource_visual_templates_tem",
                        column: x => x.template_id,
                        principalSchema: "core",
                        principalTable: "resource_visual_templates",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_resource_visual_tags_slug",
                schema: "core",
                table: "resource_visual_tags",
                column: "slug",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_resource_visual_template_revisions_template_id",
                schema: "core",
                table: "resource_visual_template_revisions",
                column: "template_id",
                unique: true,
                filter: "status = 'Draft'");

            migrationBuilder.CreateIndex(
                name: "ix_resource_visual_template_revisions_template_id_revision_no",
                schema: "core",
                table: "resource_visual_template_revisions",
                columns: new[] { "template_id", "revision_no" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_resource_visual_template_revisions_template_id_status_revis",
                schema: "core",
                table: "resource_visual_template_revisions",
                columns: new[] { "template_id", "status", "revision_no" });

            migrationBuilder.CreateIndex(
                name: "ix_resource_visual_template_tags_tag_id_template_id",
                schema: "core",
                table: "resource_visual_template_tags",
                columns: new[] { "tag_id", "template_id" });

            migrationBuilder.CreateIndex(
                name: "ix_resource_visual_templates_slug",
                schema: "core",
                table: "resource_visual_templates",
                column: "slug",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_tenant_resource_segments_tag_id_tag_kind",
                schema: "core",
                table: "tenant_resource_segments",
                columns: new[] { "tag_id", "tag_kind" });

            migrationBuilder.CreateIndex(
                name: "ix_tenant_resource_segments_tag_id_tenant_id",
                schema: "core",
                table: "tenant_resource_segments",
                columns: new[] { "tag_id", "tenant_id" });
            migrationBuilder.Sql("""
                CREATE FUNCTION core.guard_published_resource_visual_revision() RETURNS trigger
                LANGUAGE plpgsql AS $guard$
                BEGIN
                    IF OLD.status = 'Published' THEN
                        RAISE EXCEPTION 'Published visual revisions are immutable; create a new revision.'
                            USING ERRCODE = '23514';
                    END IF;
                    RETURN NEW;
                END;
                $guard$;
                CREATE TRIGGER immutable_published_resource_visual_revision
                BEFORE UPDATE ON core.resource_visual_template_revisions
                FOR EACH ROW EXECUTE FUNCTION core.guard_published_resource_visual_revision();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP TRIGGER immutable_published_resource_visual_revision ON core.resource_visual_template_revisions;
                DROP FUNCTION core.guard_published_resource_visual_revision();
                """);
            migrationBuilder.DropTable(
                name: "resource_visual_template_revisions",
                schema: "core");

            migrationBuilder.DropTable(
                name: "resource_visual_template_tags",
                schema: "core");

            migrationBuilder.DropTable(
                name: "tenant_resource_segments",
                schema: "core");

            migrationBuilder.DropTable(
                name: "resource_visual_templates",
                schema: "core");

            migrationBuilder.DropTable(
                name: "resource_visual_tags",
                schema: "core");
        }
    }
}
