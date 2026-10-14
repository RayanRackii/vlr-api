# Runbook — Platform Admin “Abrir ambiente” / sessão Auth órfã

## Sintomas
- `POST /api/admin/tenants/{id}/enter` → **400**
- Supabase Admin `GET /auth/v1/admin/users/{jwt-sub}` → **404**
- Logs: `Skipping platform admin provision … no Supabase Auth user found`

## Causa típica
Cascade delete de tenant apagou (ou deixou órfão) o Auth do PlatformAdmin. O JWT no browser ainda assina, mas o `sub` não existe mais no GoTrue.

## Correção no produto (já no código)
- Delete de tenant / user **não** remove Auth de PlatformAdmin nem de quem ainda tem membership noutro tenant.
- Enter: `ResolveOrCreateAuthUserIdAsync` recria Auth se faltar; se JWT `sub` ≠ auth resolvido → 400 pedindo reauth.
- FE: login B2B com **Esqueci a senha** → `/reset-password`.

## Runbook humano (pós-incidente)
1. Com sessão órfã, tente **Abrir ambiente** uma vez (pode recriar Auth; 400 de sessão desatualizada é esperado).
2. Logout.
3. `/login` → Esqueci a senha → definir senha.
4. Login → Abrir ambiente de novo.
5. Confirmar no Supabase Auth que `https://rolvix.com.br/reset-password` está em Redirect URLs.

## Sessão efetiva depois do exit
`POST /api/admin/tenants/exit` limpa `app_metadata.tenant_id` no Auth. O access token pode continuar com o valor antigo até o refresh. A API não usa esse claim para Super-Admin: o tenant da request é a metadata viva mais uma membership `User` ativa. Sem os dois, o request fica em modo plataforma.

## Não fazer
- Não reintroduzir header `X-Support-Tenant-Id` / abrir tenant em nova aba.
- Não deletar Auth de PlatformAdmin ao limpar um tenant.
- Não voltar a tratar `app_metadata.tenant_id` do access token como tenant do Super-Admin.

## Contexto completo da sessão
Ver [`../sessions/2026-08-05-platform-admin-membership.md`](../sessions/2026-08-05-platform-admin-membership.md).
