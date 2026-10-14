# 2026-10-14 — Super-Admin tenant update self-conflict

`PUT /api/admin/tenants/{id}` returned 409 `A tenant with the same TaxId or Subdomain already exists` when saving the tenant being edited. There was no query that excluded that id. Postgres rejected the save, and every unique violation used that message.

`TenantModule` is tenant-scoped. List, get, and update loaded modules with `Include`, so a Super-Admin JWT that still carried another `tenant_id` saw an empty module list (`Nenhum módulo ativo` on the admin card). The update then inserted the same `(tenant_id, module_name)` rows again. That unique index was reported as a TaxId/Subdomain conflict. Inside the tenant, menus still saw the original rows.

The identity check now ignores the tenant id being updated and still rejects another tenant's TaxId or Subdomain. Admin reads of that tenant's modules bypass the ambient filter and stay on that tenant's navigation. `TenantAssetFamily` sync does the same, limited to `TenantId`. No migration.

Proof: `AdminTenantUpdateConflictTests`.
