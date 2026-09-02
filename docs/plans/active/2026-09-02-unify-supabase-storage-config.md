# 2026-09-02-unify-supabase-storage-config

Status: approved

## Goal / Problem

Catalog storage used a second pair `Storage:SupabaseUrl` / `Storage:ServiceRoleKey`. Production already has `Supabase:Url` / `Supabase:ServiceRoleKey` for Auth. Duplicate keys caused silent `DevStorageProvider` in PROD.

## Confirmed decisions

- Single source: `Supabase:Url` + `Supabase:ServiceRoleKey` for project identity, including Storage HTTP and public/signed URL prefix.
- Local Development with those two set uses **real** Supabase Storage. No `Storage:UseDev`.
- `StorageOptions` only: `PublicBucket`, `PrivateBucket`, `SignedUrlTtlSeconds`.
- Remove `Storage:SupabaseUrl` / `Storage:ServiceRoleKey` completely. No legacy fallback (if only the old Storage keys are set, stay on Dev).
- Keep Production + `DevStorageProvider` → `LogError` (cite `Supabase:Url` / `Supabase:ServiceRoleKey`, no secret values).
- No HTTP/frontend contract change.

## Repositories

- vlr-api

## Implementation scope

- `StorageOptions`, `AddStorage`, `SupabaseStorageProvider` (`IOptions<SupabaseOptions>` + `IOptions<StorageOptions>`).
- `AddStorage` also `Configure<SupabaseOptions>` so DI tests that do not call `AddSupabaseAdminClient` still bind Url/key.
- Tests: creds on `Supabase:*` → `SupabaseStorageProvider` even in Development; missing → Dev; Production missing → LogError mentioning Supabase keys not Storage keys; dummy key not in logs; **legacy `Storage:SupabaseUrl`/`Storage:ServiceRoleKey` alone still Dev**.
- Docs: `appsettings.json`, runbook, this follow-up, catalog spec storage lines, hotfix spec ops table, ROADMAP Histórico + ops checkbox.
- Do not edit frontend. Do not merge (parent reports reviewer first).

## Do not

- `PublicBaseUrl`, `Storage:UseDev`, fail-start, F-05 restore, P2 product UX.
