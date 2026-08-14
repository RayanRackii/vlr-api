# ROADMAP — vlr-api

Prioridade geral: beachhead **Rentals** (clube). Ver também `CONTEXT.md` e o `ROADMAP.md` do repo irmão **`vlr-web`**.

**Foco de produto agora:** portal B2C estável + **agenda por Slot** (API pronta; UI no `vlr-web`).  
**Adiado:** fechar configuração externa Resend + WhatsApp (Meta).

## 0. Disciplina

1. Ao trabalhar neste repo: atualizar este arquivo (checklist + **Histórico** se mudou prioridade/escopo). Não apagar decisões — registre.
2. Ao encerrar uma tarefa (ou após progresso relevante), o agente deve descrever no chat o **próximo passo previsto** deste roadmap **e** do `ROADMAP.md` do **`vlr-web`**.
3. Em toda etapa concluída, descrever no chat **como testar** (passos de UI e/ou como disparar o endpoint).
4. Entregas relevantes de produto/incidentes também atualizam o diário canônico em `docs/sessions/` (um consolidado por data ou período).
5. Ordem de leitura do agente: `AGENTS.md` → `CONTEXT.md` → este arquivo → `.cursor/rules/`.

## 1. Registro dinâmico por tenant — FEITO (código)

- [x] Entidade `TenantRegistrationField` + migrations + register dinâmico + CRUD.
- [x] CPF único por tenant; limpeza duplicados FICC + seed cpf/cep/photo.
- [ ] Aplicar migrations no Railway/Supabase (`dotnet ef database update`).

## 2. Menu B2C multi-item + shell — FEITO (código)

Decisões: sidebar estilo admin; vários itens por módulo com label livre; herança = módulos ativos do tenant.

- [x] Entidade `TenantModuleMenuItem` + migration `AddTenantModuleMenuItems` (+ seed FICC “Alugar quadra”).
- [x] `GET /api/public/tenants/{subdomain}/menu`.
- [x] CRUD tenant `/api/module-menu-items` + platform `/api/admin/tenants/{id}/module-menu-items`.
- [x] Agenda B2C: assets públicos, availability, create, mine (já existia; item de menu pré-seleciona asset).
- [ ] Aplicar migration menu no Railway.
- [ ] Garantir assets/pricing no FICC para demo.
- [x] Admin B2B de reservas (listar/confirmar/cancelar).

## 2.8. Dashboard B2B dinâmico — FEITO (código)

- [x] `Customer.LastLoginAt` + migration `AddCustomerLastLoginAt` (set no login B2C).
- [x] `GET /api/dashboard/metrics` por módulo: `customerActivity` + seções nullable (`assets`, `workOrders`, `pmoc`, `maintenance`, `rentals`).
- [ ] Aplicar migration `AddCustomerLastLoginAt` no Supabase/Railway.

## 2.6. Escala diária / Slots / Layout — EM ANDAMENTO (código backend)

Decisões: ADR [`docs/adr/0001-rentals-slot-schedule.md`](./docs/adr/0001-rentals-slot-schedule.md). Glossário em `CONTEXT.md`.

- [x] `OccupancyKind` (catálogo do tenant) + defaults open/closed/lesson
- [x] `ScheduleTemplate` + `Slot` + PublishDay + UpsertSlot + BookSlot
- [x] `SchedulePolicy` SlotGrid | OpenHours em `RentalAsset`
- [x] `PUT /api/rental-assets/{id}/schedule-policy` (admin OpenHours)
- [x] `PUT /api/rental-assets/schedule-policy` (bulk transacional) + `rentalAssetIds` em GET templates/days, seed e publish
- [x] `POST /api/schedule/templates/seed-default` (bulk seed SlotGrid em 1 request)
- [x] `RentalLayout` + items (API; canvas UI pendente)
- [x] Migration `AddRentalsScheduleAndLayouts` + SQL script
- [ ] Aplicar migration no Supabase
- [x] Admin UI mínima: seed templates + publish day (no `vlr-web`)
- [x] B2C: escolher slot do dia / book por `slotId` (no `vlr-web`)
- [x] Admin UI completa: kinds, editor fino de templates
- [ ] Canvas de Layout no admin

## 2.7. Catálogo de famílias de Asset — FEITO (código)

Decisões: ADR [`docs/adr/0002-asset-families-jsonb.md`](./docs/adr/0002-asset-families-jsonb.md). Glossário: **Asset**, **AssetFamily**.

- [x] `AssetFamily` + `TenantAssetFamily` + `Asset.FamilyId` / `Attributes`
- [x] Seeds `spaces` / `electrical` / `goods` / `generic`
- [x] `GET /api/asset-families` (+ `/active`); create/update tenant com `AssetFamilyKeys`
- [x] Validação de attributes no `AssetService`
- [ ] Aplicar migration `AddAssetFamilies` no Supabase/Railway
- [ ] CRUD visual de famílias no Super-Admin (follow-up)

## 3. Notificações reais (Resend + WhatsApp) — ADIADA

- [x] Providers Resend / Meta / Dev + webhook WhatsApp.
- [ ] Config externa Meta no Railway + template Authentication.
- [ ] Provider SMS real quando sair do Dev.

## 4. Enforcement de módulos por tenant

`core.tenant_modules` existe; menu B2C já filtra por ativos. Falta middleware/filtro API → 403.

## 5. Fluxo de convite B2B real — EM ANDAMENTO

- [x] Tabela `core.user_invites` + migration `AddUserInvites`
- [x] `POST /api/admin/tenants/{id}/invites` + list users + promote + resend/revoke
- [x] `POST /api/invites/accept` (anonymous) → Supabase user + `User` + role
- [x] `GET/DELETE /api/admin/users` (lista global Super-Admin + exclusão; índices `full_name` / `tenant_id`)
- [x] Membership Super-Admin por tenant (`User` único em TenantId+SupabaseAuthId) + enter/exit via app_metadata.tenant_id
- [x] Wizard Super-Admin: passo “Admin” (nome/e-mail, sem senha)
- [x] Edit tenant: seção usuários/convites
- [x] FE `/invite` chama API real
- [x] E-mail (Resend) com layout Rolvix + `App:FrontendBaseUrl` (prod nunca emite localhost)
- [x] Reset de senha B2B: `POST /api/auth/forgot-password` → `generate_link` + Resend (`RolvixEmailLayout`); FE não usa mais `resetPasswordForEmail`
- [ ] Migrar onboarding público para invite (remover senha do admin)

## Dívidas técnicas conhecidas

- Permissions/RolePermission sem uso.
- Hangfire dashboard auth fraco em produção.
- Sem testes automatizados.
- Consulta CPF “Receita/Serpro” ainda não plugada.
- Coluna `logo_url` obsoleta (produto usa só `logo_svg`).
- Ver [`docs/code-hygiene-findings.md`](./docs/code-hygiene-findings.md) (sweep 2026-08-04).

## Histórico

| Data | Mudança |
|------|---------|
| 2026-08-03 | Beachhead clube/Rentals; portal e registro dinâmico. |
| 2026-08-04 | CPF único FICC; início agenda B2C. |
| 2026-08-04 | **Executado:** `tenant_module_menu_items` + APIs públicas/admin; seed FICC. Shell B2C no frontend. |
| 2026-08-04 | **Executado:** `LogoSvg` no Tenant + validação SVG + branding API; `LogoUrl` legado zera em writes. |
| 2026-08-04 | **Iniciado:** escala SlotGrid/OpenHours, OccupancyKind, templates, slots, layouts (API); ADR 0001. |
| 2026-08-04 | **Executado:** convite admin B2B real (user_invites + accept + UI wizard/edit). |
| 2026-08-05 | **Executado:** cascade delete seguro; users globais + índices; membership `(TenantId, SupabaseAuthId)`; enter/exit ambiente; e-mail convite sem localhost; accept find-or-create; recreate Auth órfão. Diário: [`docs/sessions/2026-08-05-platform-admin-membership.md`](./docs/sessions/2026-08-05-platform-admin-membership.md). |
| 2026-08-06 | **Docs:** `CONTEXT.md` canônico neste repo; espelho no `vlr-web`; ADR/sessions/runbooks sob `docs/`; `AGENTS.md`; rules sem paths monorepo inventados. |
| 2026-08-06 | FE (`vlr-web`): agenda B2C por Slot + admin mínimo de escala consumindo APIs já existentes. |
| 2026-08-06 | **Executado:** catálogo AssetFamily + TenantAssetFamily + Attributes JSONB; APIs + validação; ADR 0002. |
| 2026-08-06 | **Executado:** Admin B2B de reservas — `GET /api/reservations` + confirm/cancel (libera Slots). |
| 2026-08-06 | **Executado:** Dashboard dinâmico por módulo — `LastLoginAt` B2C + metrics condicionais. |
| 2026-08-06 | **Executado (FE):** Admin agenda completo — occupancy kinds CRUD + editor de templates semanais. |
| 2026-08-06 | **Executado:** Trial self-serve — campos Tenant + `trial_signup_claims`, subdomain 4 chars, limits 10/20, `TrialGuard`, Hangfire purge 30d, WhatsApp skip email-only. |
| 2026-08-09 | **Executado:** recovery B2B via Resend (`generate_link` + layout Rolvix); runbook `docs/runbooks/password-recovery-resend.md`. |
| 2026-08-09 | **Fix:** recovery e-mail usa URL first-party `?token_hash=` + `verifyOtp` (não `action_link` → Site URL `localhost:3000`). |
| 2026-08-10 | **Executado:** PlatformAdmin oculto de listas/contagens (trial); bloqueio invite/promote/delete; Auth multi-tenant preservado no delete. |
| 2026-08-10 | **Fix:** unidade padrão `Matriz` (não Headquarters); migration rename; Switch/wizard UX. |
| 2026-08-11 | **Executado:** `POST /api/schedule/templates/seed-default` + `PUT .../schedule-policy` (OpenHours admin). |
| 2026-08-14 | **Executado:** bulk `rentalAssetIds` (política transacional, GET dia/templates, seed, publish). UI no `vlr-web`: Horário padrão / Grade personalizada. Paleta fallback Rolvix (`#4D6A92` / `#5A8FA0` / `#A2C6E9`). Diário: [`docs/sessions/2026-08-14-product-delivery-log.md`](./docs/sessions/2026-08-14-product-delivery-log.md). |
| 2026-08-14 | **FE Agenda:** layout responsivo em duas colunas, filtro local de Rentables e cards acessíveis para escolha da política. |
| 2026-08-14 | **FE Agenda:** templates esclarecidos como recorrentes por `DayOfWeek`; cabeçalho centralizado e faixa redundante removida. |
| 2026-08-14 | **Perf:** `GetDayAsync` sem N+1 (reservas do dia e Slots persistidos em lote), `PublishDayAsync` sem consulta por template e `GET /api/schedule/templates?dayOfWeek=`. Leitura do dia caiu de ~170 para 5 consultas; sem mudança de schema. |
| 2026-08-14 | **Executado:** exceções diárias — `POST /api/schedule/slots/daily-occurrence` (update/indisponibilizar/restaurar), leitura admin com Cancelled + tombstones OpenHours, origem `WeeklyDefault`/`DailyOverride`. |
| 2026-08-14 | **Executado:** agenda operacional em grade — `EntireRecurrence` com cascata SlotGrid, `apply-weekly-rule`, OccupancyKind description/icon, UI virtualizada tempo×recursos. |
