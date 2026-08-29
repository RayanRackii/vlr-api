# Runbook — Remote EF migrations

Manual, fail-closed path to inspect and apply EF Core migrations for `vlr-api`. The API process does **not** migrate on startup. This machine / `railway run` is not the apply path.

## Identity (public project refs)

| GitHub Environment / workflow `target` | Supabase project ref |
|---|---|
| `development` | `jzptnjyzijklutinpxag` |
| `production` | `kbptdzfbngelzdhriyhf` |

The inspector parses `ConnectionStrings__DefaultConnection` (Username `postgres.<ref>` or Host `db.<ref>.supabase.co`) and **exits 1** with `DATABASE_TARGET_MISMATCH` if the detected ref does not match the target. It never prints the connection string, password, or tokens.

Railway environment names are independent of `ASPNETCORE_ENVIRONMENT`. This workflow does **not** use `IHostEnvironment.IsDevelopment()`.

Follow-up (not this workflow): `REVIEW_DEV_HOSTING_ENVIRONMENT` — Railway `development` currently runs with `ASPNETCORE_ENVIRONMENT=Production`. Changing that can affect notifications and other host gates, not only RBAC diagnostics.

## One-time GitHub setup

Create **GitHub Environments** named exactly:

- `development`
- `production`

Do **not** reuse the Railway-plugin environments (`marvelous-reverence / development`). Those are deploy status labels, not this workflow’s secret scope.

On each environment:

1. Add secret **`ConnectionStrings__DefaultConnection`** with that environment’s Npgsql connection string.
2. Never put the database connection string in **repository** secrets.
3. On **production**, enable **Required reviewers** in the GitHub UI (cannot be expressed in the workflow YAML). That is the Human Gate for PROD apply, in addition to typing `APPLY_PRODUCTION`.

The workflow job uses `environment: ${{ inputs.target }}`, so a `development` run cannot read the production environment secret.

## Workflow

File: `.github/workflows/database-migrations.yml`

- Trigger: **workflow_dispatch only** (no push / PR / deploy / startup).
- Inputs: `target` (`development` \| `production`), `mode` (`list` \| `apply`), `confirm_production` (required for production apply).
- Concurrency group `ef-migrate-${{ inputs.target }}` with `cancel-in-progress: false`.
- Permissions: `contents: read`.

### Always list first

1. Actions → **database-migrations** → Run workflow.
2. `target=development`, `mode=list`.
3. Read the job log for:

```text
TARGET=development
EXPECTED_REF=jzptnjyzijklutinpxag
DETECTED_REF=jzptnjyzijklutinpxag
IDENTITY=SAFE
EF_HISTORY=core.__ef_migrations_history
APPLIED
...
PENDING
...
CLIENT_ASSIGNMENTS=<n>
```

`APPLIED` / `PENDING` come from EF (`GetMigrations` vs `GetAppliedMigrations` on `core.__ef_migrations_history`). Do not trust `ROADMAP.md` as live history.

If `CLIENT_ASSIGNMENTS` is greater than zero, the log also contains `CLIENT_ROLE_USAGE_FOUND`. **Stop** before applying Tenant RBAC (`AddTenantRbacV1`) until a human decides. The list job itself still exits 0.

Other read-only counts (no PII): SuperAdmin / Technician assignments, duplicate role-name groups per tenant, orphan `UserRoles`, orphan `RolePermissions`.

### Apply development

Only after list is understood:

1. Same workflow, `target=development`, `mode=apply`.
2. Confirm `PENDING` in the log, then `MIGRATION_POSTCHECK=PASS`.
3. If pending remains: `MIGRATION_POSTCHECK_FAILED` (exit 1).

### Production Human Gate

PROD is supported by the workflow but is **not** configured or run as part of the inspector foundation.

For a future production apply, **all** of the following are required:

1. GitHub Environment `production` with Required reviewers (UI).
2. Dispatch `target=production`, `mode=apply`, `confirm_production=APPLY_PRODUCTION`.
3. Identity preflight: detected ref must be `kbptdzfbngelzdhriyhf`.

Anything else fails closed. Do not dispatch production apply by accident.

## Rollback

Rollback is **not** `dotnet ef database update` to a previous migration. That can destroy data. Restore from a Supabase backup / point-in-time recovery, or ship a new forward migration.

## Local developer machine

Do not run `dotnet ef database update` or `railway run` against DEV/PROD from an implementation laptop. Use this workflow.

The inspector CLI (CI only) reads:

- `MIGRATION_TARGET`
- `MIGRATION_MODE`
- `ConnectionStrings__DefaultConnection`
- `CONFIRM_PRODUCTION` (production apply)

It does not take the connection string as a process argument.
