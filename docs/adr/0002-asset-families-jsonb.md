# Asset families catalog + JSONB attributes (not STI / child tables)

Platform **Asset** is one table for all resource kinds. Variation (spaces vs electrical vs goods) is modeled as a platform **AssetFamily** catalog with a **FieldSchema** JSON document; each Asset stores validated **Attributes** (JSONB) for that family’s fields. Tenants opt in via **TenantAssetFamily** at onboarding. Fine-grained grouping inside a family remains **AssetCategory** (Tipo).

**Status:** accepted

**Considered options:** (1) separate physical tables / STI per use case (`spaces`, `electrical_assets`, …) — rejected (migration explosion, AI/schema drift); (2) free-form ExtraAttributes with no schema — rejected (forms and validation become ad hoc); (3) family catalog + JSONB attributes validated by FieldSchema — accepted.
