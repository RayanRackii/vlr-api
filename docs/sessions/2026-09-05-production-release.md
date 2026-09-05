# 2026-09-05 — Production release (Asset Registry / module gates)

Human Gate approved. Coordinated squash: API first, then WEB. No migrations, backfill, permission seed, or new PROD config.

| | SHA |
|---|---|
| API `main` | `48ad32a1e2ac3c71ec7df59a895ef1eecae55140` (PR #51) |
| WEB `main` | `37a5266381ad5061cfda0299acb2c84a2726b050` (PR #48) |
| API rollback | `575adb205c8eff856d67c79d31d4bbc75a9eeed6` |
| WEB rollback | `4b048c4f0e4f4a54efc5dca74404627699b9259d` |

- Railway production: SUCCESS; `GET /health` → 200.
- Vercel Production: SUCCESS; apex 308 → `www.rolvix.com.br` 200.
- DEV E2E_CERTIFIED: 51 passed, 32/32 combinations, `RESTORE_MATCH YES`.
- Authenticated PROD UI smoke: not executed (no safe PlatformAdmin/B2B fixture).
- Public B2C: known tenant portal host + public branding/menu/rental-assets 200; menu has `rentals`, no `asset-registry`.
