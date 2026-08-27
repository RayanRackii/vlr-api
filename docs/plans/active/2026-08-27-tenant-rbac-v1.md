# 2026-08-27-tenant-rbac-v1

Status: approved

## Goal / Problem

Replace ad-hoc `Role == Admin` / `Role == Technician` checks and the no-op `POST /api/users/invite` stub with tenant-scoped Roles + Permissions. Users have 1..N roles; effective permissions = UNION; gated by active tenant modules. PlatformAdmin and B2C Customer stay outside this layer.

## Visible behavior

- Tenant admins manage users and custom roles from **Pessoas e acesso**.
- Tenant admins invite users with 1..N roles; PlatformAdmin invite path still works.
- `GET /api/users/me` keeps legacy `role` and adds `roles[]` (objects) + `permissions[]`.
- B2B module endpoints enforce a specific permission (403 `{ "error": "..." }`).
- Last active Admin cannot be demoted/deleted (`LAST_ADMIN_PROTECTED`).
- Privilege escalation blocked (`PRIVILEGE_ESCALATION_BLOCKED`).
- Disabled module → module permissions have no effect (including Admin wildcard).
- B2C Customer and PlatformAdmin policy/allowlist/enter-exit unchanged.

## Repositories

- vlr-api
- vlr-web

## Architecture route

- rolvix-architect (approved)
- rolvix-deep-architect: **Fable Merge Risk Gate mandatory** at merge (auth + tenant + FE↔BE contract). Not invoked during implementation.

## Execution route

- api-implementer first (contract)
- web-implementer after API `/me` additive contract exists on the feature branch
- Parent: review, dossier, Fable, PRs, merge to `develop` only

## Confirmed decisions

See user prompt Tenant RBAC v1. Do not reopen PlatformAdmin, B2C, union permissions, system Admin/User, no override_conflict, fail-closed, request-scoped memo only, invite additive, dashboard legacy role branching.

`/me.roles` is an **array of objects** `{ id, name, isSystemRole }` (user contract), not a string array.

`GET /api/permissions` is the catalog endpoint (not `/api/roles/permissions`).

User default bundle is the conservative list in the user prompt (reads + `os.work_orders.execute`). Do **not** grant `core.users.*`, `core.roles.*`, `core.registration_fields.*`, or `core.module_menu.*` to User by default.

## Invariants

1. Default `[Authorize]` still rejects Customer JWTs.
2. Customer + PlatformAdmin policies unchanged.
3. Public `AllowAnonymous` unchanged.
4. Resolver takes explicit `tenantId`; never leak RolePermission across tenants.
5. Membership remains `core.users`. No `tenant_memberships`.
6. Role SoT = DB, not JWT.
7. Tenants cannot create permission keys.
8. Existing `UserInvite.RoleName` still accepts.
9. Do not delete SuperAdmin/Technician/Client rows.
10. PlatformAdmin enter tenant still auto-grants Admin membership.

## Permission catalog (37 keys)

`ModuleKey` null = core (no `TenantModule` gate).

| Key | ModuleKey |
|---|---|
| core.dashboard.read | (null) |
| core.users.read | (null) |
| core.users.invite | (null) |
| core.users.assign_roles | (null) |
| core.roles.read | (null) |
| core.roles.manage | (null) |
| core.units.read | (null) |
| core.registration_fields.read | (null) |
| core.registration_fields.write | (null) |
| core.module_menu.read | (null) |
| core.module_menu.write | (null) |
| inventory.assets.read | inventory |
| inventory.assets.write | inventory |
| inventory.categories.read | inventory |
| inventory.categories.write | inventory |
| inventory.families.read | inventory |
| pmoc.plans.read | pmoc |
| pmoc.plans.write | pmoc |
| pmoc.templates.read | pmoc |
| os.work_orders.read | os |
| os.work_orders.create | os |
| os.work_orders.execute | os |
| os.work_orders.assign | os |
| rentals.reservations.read | rentals |
| rentals.reservations.confirm | rentals |
| rentals.reservations.cancel | rentals |
| rentals.schedule.read | rentals |
| rentals.schedule.write | rentals |
| rentals.assets.read | rentals |
| rentals.assets.write | rentals |
| rentals.pricing.read | rentals |
| rentals.pricing.write | rentals |
| rentals.pricing.bulk_write | rentals |
| rentals.layouts.read | rentals |
| rentals.layouts.write | rentals |
| rentals.occupancy_kinds.read | rentals |
| rentals.occupancy_kinds.write | rentals |

No `core.units.write` (UnitsController is GET-only). No B2C/PlatformAdmin keys. No `rentals.reservations.override_conflict`. `os.work_orders.assign` is seeded; WorkOrdersController has no assign action yet — used when listing eligible assignees.

Constants: `Core/Platform.Core.Domain/Constants/Permissions.cs` nested static classes. Persist in `core.permissions`.

## System role bundles

- **Admin** (`IsSystemRole`): cannot rename/delete; permissions not reducible. Resolver **wildcard**: all catalog keys whose module is enabled (core always). Persist full `RolePermission` set for UI; bootstrap adds new keys later. Not PlatformAdmin.
- **User** (`IsSystemRole`): cannot rename/delete; permissions **editable**. Seed:

  `core.dashboard.read`, `core.units.read`,
  `inventory.assets.read`, `inventory.categories.read`, `inventory.families.read`,
  `pmoc.plans.read`, `pmoc.templates.read`,
  `os.work_orders.read`, `os.work_orders.execute`,
  `rentals.reservations.read`, `rentals.schedule.read`, `rentals.assets.read`,
  `rentals.pricing.read`, `rentals.layouts.read`, `rentals.occupancy_kinds.read`

- **SuperAdmin** (legacy): keep rows; same wildcard as Admin; do not offer in tenant role picker; do not create new via tenant UI.
- **Technician** (legacy): keep rows; if role exists, seed `os.work_orders.read`, `os.work_orders.execute`, `inventory.assets.read` (idempotent, do not remove extra perms). Eligibility = `os.work_orders.execute`, never `Role.Name == Technician`.
- **Client** (legacy): keep rows; do not create new. DEV hosted diagnostic logs count of active UserRoles with Role.Name Client. If count > 0, log `USER_DECISION_REQUIRED` — do **not** auto-delete; do **not** stop enforcement unless the count shows real product usage that would lock out users (then stop with `USER_DECISION_REQUIRED`). Isolated leftover rows: log and continue.

## IPermissionResolver

`Platform.Api/Authorization/IPermissionResolver.cs`

```csharp
IReadOnlySet<string> GetEffectivePermissions(Guid tenantId, Guid userId);
bool HasPermission(Guid tenantId, Guid userId, string permissionKey);
```

- Explicit `tenantId`. Load membership `User` for `(tenantId, userId)` with query filter or explicit predicate — never another tenant’s roles.
- Inactive user → empty set.
- Admin/SuperAdmin system role → wildcard catalog filtered by `ModuleEnabled`.
- Else UNION of `RolePermissions.Permission.Key` filtered by `ModuleEnabled`.
- `ModuleKey` null/`core` → enabled. Else `TenantModule.IsActive` for that tenant.
- Request-scoped memo `Dictionary<(Guid tenantId, Guid userId), IReadOnlySet<string>>`. No Redis.
- Fail closed: exception / missing user / missing tenant → deny.

## ASP.NET

- `RequirePermissionAttribute` : `AuthorizeAttribute`, `Policy = "perm:" + key`.
- `IAuthorizationPolicyProvider` builds policy on demand: **default B2B policy** (authenticated + not Customer) **AND** `PermissionRequirement`.
- `PermissionAuthorizationHandler`: fail closed. PlatformAdmin **with tenant context** succeeds (wildcard). PlatformAdmin **without** tenant → fail on tenant endpoints.
- Do not register hundreds of named policies.

## TenantAccessBootstrapper

`EnsureAsync(tenantId)`:

1. Seed missing `core.permissions` from catalog (idempotent).
2. Ensure Admin + User system roles.
3. Ensure Admin RolePermissions = full catalog.
4. Ensure User RolePermissions contains default User bundle (never remove tenant-edited extras).
5. If Technician role exists, ensure its legacy execute bundle (additive).

Call from: CreateTenantHandler, AdminTenantService.Create, PlatformAdminMembershipService (before Admin assign), and migration data seed.

## Migration `AddTenantRbacV1`

Additive:

1. `core.user_invite_roles` (`invite_id`, `role_id`, PK composite, FKs cascade).
2. Seed `core.permissions` `INSERT ... ON CONFLICT (key) DO NOTHING`.
3. Per tenant: ensure Admin + User; backfill RolePermissions as above; SuperAdmin gets same RolePermission set as Admin (UI); Technician bundle if role exists.
4. Do not delete roles or user_roles.

`UserInvite.RoleName` stays required for legacy.

## Errors (`{ "error": "<CODE>" }`)

| Code | HTTP |
|---|---|
| PRIVILEGE_ESCALATION_BLOCKED | 403 |
| LAST_ADMIN_PROTECTED | 409 |
| ROLE_IN_USE | 409 |
| CANNOT_MODIFY_SYSTEM_ROLE | 409 |
| CANNOT_DELETE_SYSTEM_ROLE | 409 |
| CANNOT_ASSIGN_SUPERADMIN | 403 |
| FORBIDDEN | 403 |

Last Admin: count **active** users with system role Admin **or** SuperAdmin. Block removing last such membership.

Privilege escalation: requested role’s effective perms ⊆ actor effective perms. Assigning Admin/SuperAdmin requires actor to already be Admin/SuperAdmin **or** PlatformAdmin-in-tenant. PlatformAdmin-in-tenant bypasses escalation.

## GET /api/users/me

Keep all existing fields including `role` (flattened ApplicationRoles string).

Add:

```json
"roles": [{ "id": "...", "name": "Admin", "isSystemRole": true }],
"permissions": ["core.dashboard.read"]
```

PlatformAdmin outside tenant: `role=SUPER_ADMIN`, `roles=[]`, `permissions=[]`.
PlatformAdmin in tenant with membership: real roles + wildcard/effective perms; `role` stays `ADMIN`.

## APIs

Roles (tenant-scoped):

- GET `/api/roles` — `core.roles.read`
- GET `/api/roles/{id}` — `core.roles.read`
- POST `/api/roles` — `core.roles.manage` body `{ name, description?, permissionKeys[] }`
- PATCH `/api/roles/{id}` — `core.roles.manage`
- PUT `/api/roles/{id}/permissions` — `core.roles.manage` body `{ permissionKeys[] }`
- DELETE `/api/roles/{id}` — `core.roles.manage`

GET `/api/permissions` — `core.roles.read` — catalog `{ key, name, description, moduleKey, resource }` groupable.

Users:

- GET `/api/users/me` — `[Authorize]` only
- GET `/api/users` — `core.users.read` (id, fullName, email, isActive, roles[])
- GET `/api/users/technicians` — `core.users.read` — users with `os.work_orders.execute` (not role name)
- PUT `/api/users/{userId}/roles` — `core.users.assign_roles` `{ roleIds: Guid[] }`
- POST `/api/users/invite` — `core.users.invite` `{ fullName, email, roleIds: Guid[] }` (1..N). Replaces stub. Remove `MapInviteUserEndpoint`.

PlatformAdmin `POST /api/admin/tenants/{id}/invites` stays PlatformAdmin policy. Additive optional `roleIds[]`; keep `roleName`.

Accept invite: if `UserInviteRole` rows exist, assign those; else `RoleName` (legacy).

Custom role delete while assigned → 409 `ROLE_IN_USE`. Unique name per tenant.

## Controller permission mapping

Replace class-level `[Authorize]` with `[RequirePermission]` on B2B actions (policy includes B2B default).

| Action | Permission |
|---|---|
| DashboardController.GetMetrics | core.dashboard.read |
| UsersController.GetCurrent | (none, Authorize) |
| UsersController.ListTechnicians / List | core.users.read |
| UsersController.AssignRoles | core.users.assign_roles |
| UsersController.Invite | core.users.invite |
| RolesController GET | core.roles.read |
| RolesController mutate | core.roles.manage |
| PermissionsController GET | core.roles.read |
| UnitsController.List | core.units.read |
| RegistrationFields List | core.registration_fields.read |
| RegistrationFields CUD | core.registration_fields.write |
| ModuleMenuItems List | core.module_menu.read |
| ModuleMenuItems CUD | core.module_menu.write |
| Assets List/Get | inventory.assets.read |
| Assets CUD/Bulk | inventory.assets.write |
| AssetCategories List/Get | inventory.categories.read |
| AssetCategories CUD | inventory.categories.write |
| AssetFamilies List/Active | inventory.families.read |
| MaintenancePlans List/Get | pmoc.plans.read |
| MaintenancePlans CUD | pmoc.plans.write |
| GlobalTemplates List | pmoc.templates.read |
| WorkOrders List/Get | os.work_orders.read |
| WorkOrders Create | os.work_orders.create |
| WorkOrders UpdateTask/Status | os.work_orders.execute |
| Reservations ListAdmin | rentals.reservations.read |
| Reservations Confirm | rentals.reservations.confirm |
| Reservations Cancel | rentals.reservations.cancel |
| Schedule GET templates/days (admin) | rentals.schedule.read |
| Schedule writes (templates CUD, seed, weekly, publish, slots, daily-occurrence) | rentals.schedule.write |
| RentalAssets List/GetByAsset | rentals.assets.read |
| RentalAssets schedule-policy | rentals.assets.write |
| RentalPricings List | rentals.pricing.read |
| RentalPricings CUD | rentals.pricing.write |
| Pricing bulk | rentals.pricing.bulk_write |
| OccupancyKinds List | rentals.occupancy_kinds.read |
| OccupancyKinds CUD | rentals.occupancy_kinds.write |
| RentalLayouts List/Get | rentals.layouts.read |
| RentalLayouts CUD | rentals.layouts.write |

Remove `EnsureTenantAdminAsync` and `currentUser.Role == Admin` / `Technician` authorization. Keep OS **DomainRule**: Technician/User-equivalent execute users only see/update **assigned** work orders unless they also have `os.work_orders.create` or Admin wildcard (users with only execute → assigned-only). Prefer: if HasPermission create **or** roles include Admin/SuperAdmin wildcard → all OS; if only execute → assigned-only. Do not use role name.

Stay `[Authorize]` / Customer / PlatformAdmin / Anonymous as today for: `/me`, CustomerAuth, CustomerProfile, reservations mine/create, availability, public assets/schedule/layouts/menu, queue Customer, invite accept, webhooks, password recovery, all `/api/admin/*`.

## Frontend

- Extend `currentUserSchema`: `roles: z.array({ id, name, isSystemRole }).default([])`, `permissions: z.array(z.string()).default([])`. Keep `role`.
- `usePermissions()` / `can(key)` / `<Can permission>` / `PermissionRoute` (403 UX, not only hide).
- Nav: module AND permission.
- Page **Pessoas e acesso** `/pessoas-e-acesso` tabs Usuários | Funções e permissões. i18n Função/Permissões (not “Role”).
- Role editor: Admin readonly full; User name/delete locked, perms editable; custom CRUD. Inactive module: “Módulo não ativo para este tenant”.
- Users: name, email, status, roles (multi). PUT roleIds.
- Dashboard: keep legacy `role` branching; no new role checks.
- Add **vitest** + testing-library for `can()`, PermissionRoute, Can (auth is high-severity; TEST_INFRASTRUCTURE_MISSING otherwise blocks).

## Tests (API, xunit, existing InMemory/Postgres)

Required cases from user §28–29. Extend `RolvixAuthorizationPolicyTests` so Customer/PlatformAdmin/default still pass.

## Structured logs

role created/updated/deleted, permissions changed, user roles changed, invitation created/accepted, privilege-escalation rejected, last-admin rejected. Never log JWT, invite token, passwords.

## Do not

B2C JWT/policy/customerApi; PlatformAdmin allowlist; tenant_memberships; JWT permission claims; Redis cache; override_conflict; persistent AuditLog; PROD/main; auto-delete Client; new role-name checks; MediatR expansion.

## Merge order

API first (additive `/me`). Then Web. Not COORDINATED_MERGE_REQUIRED if `/me` stays additive.

## CONTEXT_PACK_UPDATE_RECOMMENDED

After implementation: `platform-core` + `authentication` packs. ADR next number **0004** (0002 already asset families).
