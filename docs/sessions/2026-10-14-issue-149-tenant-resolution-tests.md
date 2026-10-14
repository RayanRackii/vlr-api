# 2026-10-14 — Tenant resolution tests (#149)

Follow-up from #146. Platform-admin tenant context still comes from live Auth `app_metadata` plus an active membership. These tests cover the edges that review left open.

Auth admin timeout or any other read failure resolves the session with no tenant. A stale access-token `tenant_id` is not used, and `GET /api/customers` stays unauthorized. `IsActive = false` does the same even when live metadata still names that tenant. After the admin has entered a tenant, deleting that tenant and its membership (live metadata still points at the deleted id) returns platform mode: no crash, and the other tenant's customers are not returned.

A `sub` that is not an auth id (`not-a-guid`, blank, missing, or path-shaped) does not throw and does not select a tenant. Blank or path-shaped subjects do not call Auth admin. `ShouldResolve` now states that a blank subject still resolves the session, with the auth id left empty.

No behavior change beyond that comment and the split of the existing branch. No migration. Draft until the GLM/Fable gate.
