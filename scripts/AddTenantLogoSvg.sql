-- Apply in Supabase SQL Editor if `dotnet ef database update` cannot reach the pooler.
-- Migration: 20260804152356_AddTenantLogoSvg

ALTER TABLE core.tenants
  ADD COLUMN IF NOT EXISTS logo_svg text NULL;

-- Record EF history (only if this migration is not already applied):
INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
SELECT '20260804152356_AddTenantLogoSvg', '10.0.0'
WHERE NOT EXISTS (
  SELECT 1 FROM "__EFMigrationsHistory"
  WHERE "MigrationId" = '20260804152356_AddTenantLogoSvg'
);
