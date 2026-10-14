# 2026-10-14 — Super-Admin delete under another tenant

`DELETE /api/admin/tenants/{id}` loaded and removed child rows through the ambient global query filter. After `POST /api/admin/tenants/{A}/enter`, deleting tenant B matched no children of B. Users, units, and roles (`ON DELETE RESTRICT`) blocked the tenant delete. Tables without a tenant foreign key would remain after the tenant row was removed.

`DeleteAsync` now ignores that filter and keeps `TenantId == id` on every child delete, including the membership read that decides which Supabase auth ids to remove. An auth id still used by another tenant is left in place. No migration.
