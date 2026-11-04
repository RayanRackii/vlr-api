# Catalog public file reconciliation

Operator/admin CLI only: requires access to the API deployment's database connection and
Supabase service-role storage credentials. There is no HTTP endpoint or customer/admin UI route.
Use the usual `ConnectionStrings__DefaultConnection`, `Supabase__Url`,
`Supabase__ServiceRoleKey` and `Storage` configuration; do not paste credentials into logs.
The command builds only persistence/storage dependencies and never starts the web host,
Hangfire, seeders or migrations. Operators must authorize the target environment separately.

After applying `20261104010000_AddCatalogProductFileStatus`, inventory public storage:

```sh
dotnet Platform.Api.dll catalog-files-reconcile
```

This defaults to report-only. Review each escaped object key, tenant/product/file identifier,
classification/action, missing-private warnings and final counts. Only apply after reviewing
that report and confirming the configured buckets and database belong to the intended environment:

```sh
dotnet Platform.Api.dll catalog-files-reconcile --apply
```

- `legacy-ok`: an existing Public product's CustomerVisible file (including images) is flagged
  `is_legacy_public=true` only when the public object actually exists.
- `restricted-public` / `internal-public`: remove the public object and clear the flag. If no
  private object exists, mark the file `Failed`; it cannot be served. No copy is fabricated.
- `orphan`: remove objects with no matching tenant/product/file row, deleted products or malformed
  catalog keys. The explicit `{tenantN}/work-orders/...` namespace is logged as `skipped`.
  Other unrecognized keys are treated as orphans; inspect the report before applying.
- `flag-mismatch`: clear a legacy flag when the public object is absent; also fail unsafe files
  without a private copy, allowing recovery from an interrupted prior reconciliation.

Writes reselect by tenant and hold a tenant/product `FOR UPDATE` lock through storage deletion
and commit. Listing completes before deletions, so pagination cannot skip objects removed by this
run. A second successful apply performs no changes. Successful completion exits 0; operational
failures propagate and require inspection/retry. Run with catalog writes paused for a complete
inventory: object storage listings are not a transactionally consistent snapshot of active uploads.

Migration Up only adds `status` (Ready default) and `is_legacy_public` (false default); it never
infers location from visibility. Down conservatively refuses a populated file table under an
exclusive lock, since no Up-time marker exists to distinguish later private Ready rows. This
protects Pending/Failed state and storage provenance across Down/Up. Empty-table rollback is safe.
Catalog portal and admin signed URLs expire after 120 seconds; previously issued URLs remain
usable until their expiration after access revocation. This command has not been run on any
external environment by this change.
