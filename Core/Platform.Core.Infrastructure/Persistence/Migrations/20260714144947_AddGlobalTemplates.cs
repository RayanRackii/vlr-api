using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace Platform.Core.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddGlobalTemplates : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "global_maintenance_templates",
                schema: "pmoc",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    description = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    frequency = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    jurisdiction = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    target_equipment_type = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_global_maintenance_templates", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "global_template_tasks",
                schema: "pmoc",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    global_maintenance_template_id = table.Column<Guid>(type: "uuid", nullable: false),
                    title = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    input_type = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    configuration = table.Column<string>(type: "jsonb", nullable: true),
                    is_mandatory = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    order = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_global_template_tasks", x => x.id);
                    table.ForeignKey(
                        name: "fk_global_template_tasks_global_maintenance_templates_global_m",
                        column: x => x.global_maintenance_template_id,
                        principalSchema: "pmoc",
                        principalTable: "global_maintenance_templates",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.InsertData(
                schema: "pmoc",
                table: "global_maintenance_templates",
                columns: new[] { "id", "created_at", "description", "frequency", "jurisdiction", "name", "target_equipment_type", "updated_at" },
                values: new object[] { new Guid("6f1c2a0e-4b9d-4f3a-9c7e-1d2a3b4c5d6e"), new DateTimeOffset(new DateTime(2026, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)), "Modelo base alinhado à Lei 13.589/2018, Resolução Anvisa RE 09 e inspeções elétricas da NR-10 para equipamentos de climatização.", "Monthly", "BR", "PMOC Padrão ANVISA (RE 09) + NR-10", "Ar Condicionado", null });

            migrationBuilder.InsertData(
                schema: "pmoc",
                table: "global_template_tasks",
                columns: new[] { "id", "configuration", "created_at", "global_maintenance_template_id", "input_type", "is_mandatory", "order", "title", "updated_at" },
                values: new object[,]
                {
                    { new Guid("7a2b3c4d-5e6f-4789-a012-3456789abc01"), null, new DateTimeOffset(new DateTime(2026, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)), new Guid("6f1c2a0e-4b9d-4f3a-9c7e-1d2a3b4c5d6e"), "Image", true, 1, "Foto do Local de Instalação (Visão Geral do Ambiente)", null },
                    { new Guid("7a2b3c4d-5e6f-4789-a012-3456789abc02"), null, new DateTimeOffset(new DateTime(2026, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)), new Guid("6f1c2a0e-4b9d-4f3a-9c7e-1d2a3b4c5d6e"), "Image", true, 2, "Foto Frontal do Equipamento (Evaporadora)", null },
                    { new Guid("7a2b3c4d-5e6f-4789-a012-3456789abc03"), null, new DateTimeOffset(new DateTime(2026, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)), new Guid("6f1c2a0e-4b9d-4f3a-9c7e-1d2a3b4c5d6e"), "Image", true, 3, "Foto do Quadro Elétrico / Painel de Comando (NR-10)", null },
                    { new Guid("7a2b3c4d-5e6f-4789-a012-3456789abc04"), null, new DateTimeOffset(new DateTime(2026, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)), new Guid("6f1c2a0e-4b9d-4f3a-9c7e-1d2a3b4c5d6e"), "Checkbox", true, 4, "Limpeza da bandeja de drenagem e serpentina", null },
                    { new Guid("7a2b3c4d-5e6f-4789-a012-3456789abc05"), "{\"unit\":\"A\"}", new DateTimeOffset(new DateTime(2026, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)), new Guid("6f1c2a0e-4b9d-4f3a-9c7e-1d2a3b4c5d6e"), "Number", true, 5, "Medição da Corrente de Operação (Compressor)", null },
                    { new Guid("7a2b3c4d-5e6f-4789-a012-3456789abc06"), "{\"options\":[\"Íntegro\",\"Danificado\",\"Inexistente\"]}", new DateTimeOffset(new DateTime(2026, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)), new Guid("6f1c2a0e-4b9d-4f3a-9c7e-1d2a3b4c5d6e"), "SingleChoice", true, 6, "Estado do isolamento térmico", null }
                });

            migrationBuilder.CreateIndex(
                name: "ix_global_maintenance_templates_jurisdiction",
                schema: "pmoc",
                table: "global_maintenance_templates",
                column: "jurisdiction");

            migrationBuilder.CreateIndex(
                name: "ix_global_maintenance_templates_name",
                schema: "pmoc",
                table: "global_maintenance_templates",
                column: "name");

            migrationBuilder.CreateIndex(
                name: "ix_global_template_tasks_global_maintenance_template_id_order",
                schema: "pmoc",
                table: "global_template_tasks",
                columns: new[] { "global_maintenance_template_id", "order" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "global_template_tasks",
                schema: "pmoc");

            migrationBuilder.DropTable(
                name: "global_maintenance_templates",
                schema: "pmoc");
        }
    }
}
