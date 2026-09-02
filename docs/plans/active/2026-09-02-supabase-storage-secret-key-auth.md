# 2026-09-02-supabase-storage-secret-key-auth

Status: approved

## Goal / Problem

`SupabaseStorageProvider` sends only `Authorization: Bearer <Supabase:ServiceRoleKey>`. Railway DEV uses opaque `sb_secret_...`. Storage treats it as a JWT → 400 → `EnsureSuccessStatusCode()` → API 500.

## Confirmed decisions

- `sb_secret_...`: `apikey` only; **no** `Authorization: Bearer`.
- Legacy JWT (`eyJ...`): `apikey` + `Authorization: Bearer` (same value).
- `sb_publishable_...`: invalid privileged backend credential → fail closed (config).
- Shared helper for Auth Admin + Storage header construction (no extra architecture).
- Capture Storage error body; never log credentials/auth headers.
- Map auth/Invalid JWT/401/403 to **server/upstream**, not user 400. Duplicate object → 409. Known file/mime/size Storage rejections → 400. Other unexpected → controlled 5xx. Shape `{ error: string }`.
- No HTTP contract expansion, no Storage__ URL/key, no CatalogFileRules/key/bucket/signed-URL/options changes, no migration, no WEB, no main/PROD.

## Repositories

- vlr-api

## Architecture / execution

- api-implementer
- api-reviewer
- Merge Risk Gate: **Fable required** (privileged credential + external auth + error mapping)

## Implementation notes

Helper in `Platform.Core.Infrastructure.Supabase` (Auth Admin lives there). Classify by prefix (`sb_secret_`, `sb_publishable_`, JWT `eyJ`). Unknown format → config invalid.

Apply on Storage `HttpClient.DefaultRequestHeaders` at construction (covers upload/sign/delete). Auth Admin `CreateAdminRequest` uses the same helper.

Upload (and other methods that currently `EnsureSuccessStatusCode`): read body first; parse `statusCode` / `error` / `code` / `message` when JSON; sanitize (truncate, strip key-like tokens). Log status, code, sanitized message, bucket, object key.

## Tests

See user instruction (headers, publishable reject, success, 400 body capture, invalid-JWT → not 400, malformed body, credentials absent from logs/errors).

## Do not

WEB, main, Railway, buckets, Twilio, WhatsApp, migrations, `Storage:UseDev`, `PublicBaseUrl`.
