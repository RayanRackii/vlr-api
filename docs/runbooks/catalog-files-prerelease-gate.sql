-- any non-zero count = STOP and tell Rayan before PROD
-- Read-only gate, compatible with schemas before and after AddCatalogProductFileStatus.
-- Persisted values: products Restricted; files CustomerVisible / InternalB2B.
-- CatalogProduct has no soft-delete columns (deleted_at/is_deleted).
-- is_active is activation state, not deletion; only missing products are checked.
BEGIN READ ONLY;

SELECT 'restricted_products_with_customer_visible_files' AS check_name,
       count(*) AS violation_count
FROM catalog.catalog_products AS p
WHERE to_jsonb(p)->>'visibility' = 'Restricted'  -- to_jsonb: works before the visibility column exists (PROD pre-release)
  AND EXISTS (
      SELECT 1 FROM catalog.catalog_product_files AS f
      WHERE f.product_id = p.id AND f.tenant_id = p.tenant_id
        AND f.visibility = 'CustomerVisible'
  );

SELECT 'files_with_unexpected_visibility' AS check_name,
       count(*) AS violation_count
FROM catalog.catalog_product_files AS f
WHERE f.visibility IS NULL OR f.visibility NOT IN ('CustomerVisible', 'InternalB2B');

SELECT 'files_with_missing_product' AS check_name,
       count(*) AS violation_count
FROM catalog.catalog_product_files AS f
LEFT JOIN catalog.catalog_products AS p
  ON p.id = f.product_id AND p.tenant_id = f.tenant_id
WHERE p.id IS NULL;

ROLLBACK;
