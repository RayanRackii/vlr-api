# Rentals RLS and tenant authorization: static finding

Date: 2026-10-08  
Scope: local source plus read-only Supabase catalog/grant queries in production and dev. No live database was changed.

## Finding

The source migrations/configuration reviewed do not enable PostgreSQL Row Level Security or define policies for these 11 `rentals` tables:

1. `rentals.rental_assets`
2. `rentals.reservations`
3. `rentals.reservation_items`
4. `rentals.rental_pricings`
5. `rentals.layouts`
6. `rentals.layout_items`
7. `rentals.occupancy_kinds`
8. `rentals.schedule_templates`
9. `rentals.slots`
10. `rentals.reservation_queue_sessions`
11. `rentals.reservation_queue_tickets`

Read-only live checks on 2026-10-08 confirmed `relrowsecurity=false` for these tables in both the production project (`RLV solutions`) and the dev project (`Rolvix DEV`). Direct privilege checks also found `anon` and `authenticated` have no SELECT/INSERT/UPDATE/DELETE table privileges on the 11 tables in either project. Therefore the Supabase tool's generic advisory that RLS-off tables are exposed to anon/auth is not supported by the actual grants checked here. The deployed backend connection role and all of its effective grants were not identified, so this does not prove backend access is appropriately constrained.

## Existing backend controls evidenced in source

- `AppDbContext.OnModelCreating` applies a global tenant query filter to every entity implementing `ITenantScoped`. Its predicate is `CurrentTenantId == null || entity.TenantId == CurrentTenantId`; therefore a missing tenant context intentionally removes that filter and must be handled by the calling service/background job.
- `Platform.Api/Program.cs` registers `HttpContextTenantProvider` for API requests and configures EF persistence from `ConnectionStrings:DefaultConnection`. This source does not establish the actual database identity or its grants.
- Rentals services also use explicit tenant predicates for sensitive reads and mutation paths; the new teacher lesson service checks tenant-owned asset/slot/template records and reservation rows. Its reservation check joins `rentals.reservation_items` to `rentals.reservations` with tenant predicates, rather than trusting only slot status.
- The new asset-lock helper includes both rental asset ID and tenant ID in its row-lock SQL. Other raw SQL still requires individual review; EF query filters do not protect raw SQL automatically.

## Limits and follow-up

- No blanket RLS enablement is proposed in this feature. Enabling RLS safely requires a separate design for policies, connection roles, background jobs, migrations, and integration tests.
- A read-only production census found exactly six active, rentable FICC `Location` assets using `SlotGrid` (Q-1 through Q-6). No FICC tenant was found in the dev project. The feature's server-side scope is fixed to the six production `rental_asset` IDs and the FICC tenant ID; a newly created court or a same-ID asset in another tenant is rejected. Expanding the pilot requires an explicit source change and review.
- Before enabling direct client access or granting broad SQL privileges, identify the backend connection role and inspect its effective production/dev grants in a separately approved read-only audit.
- The teacher lesson feature does not change RLS and must continue enforcing tenant scope in the API.
