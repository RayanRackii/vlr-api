using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Platform.Core.Infrastructure.Persistence.Migrations;

/// <summary>
/// Data only (review P2). The seeded "goods" asset family must carry a pt-BR label with no English
/// word in it. Some databases hold the label "Quantidade típica (optional)" (the suffix was added by
/// hand or by an older client); this puts back the seeded text. Guarded: it touches only the "goods"
/// family and only when the old text is present, so a database that never had it, or whose admin chose
/// another label, is left alone. Idempotent. No model change; the snapshot is unchanged.
/// </summary>
public partial class CorrectSeededGoodsFamilyFieldLabel : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            UPDATE assets.asset_families
            SET field_schema_json = replace(field_schema_json::text, 'Quantidade típica (optional)', 'Quantidade típica')::jsonb
            WHERE key = 'goods'
              AND field_schema_json::text LIKE '%Quantidade típica (optional)%';
            """);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        // Guarded no-op: the previous text was a defect and is not restored.
    }
}
