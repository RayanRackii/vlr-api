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
- [x] `POST /api/schedule/templates/seed-default` (bulk seed SlotGrid em 1 request; força política SlotGrid)
- [x] Day read deriva SlotGrid não publicado a partir dos templates do weekday (B2C reserva sem PublishDay)
- [x] `RentalLayout` + items (API; canvas UI no `vlr-web` em Operação)
- [x] Migration `AddRentalsScheduleAndLayouts` + SQL script
- [x] `RentalAsset.RequiresDeposit` (default true): BookSlot/CreateReservation nasce `PendingDeposit` ou `Confirmed`
- [ ] Aplicar migrations no Supabase (`AddRentalsScheduleAndLayouts`, `AddRentalAssetRequiresDeposit`)
- [x] Admin UI mínima: seed templates + publish day (no `vlr-web`)
- [x] B2C: escolher slot do dia / book por `slotId` ou create-reservation (SlotGrid derivado)
- [x] Admin UI completa: kinds, editor fino de templates
- [x] Canvas de Layout no admin (`vlr-web` Operação) + picker B2C data+horário
- [x] Trial expirado: `BookSlot` / `CreateReservation` passam por `TrialGuard` (mesmo guard de Confirm/Cancel)
- [x] F-01: `SELECT … FOR UPDATE` em `RentalAsset` serializa `CreateReservation` / `BookSlot` (prova em Testcontainers; 2026-08-22: `ReservationConcurrencyTests` 2 passed / 0 skipped com Docker 29.5.3)
- [x] F-10: overlap de `ScheduleTemplate` entre OccupancyKinds; UNIQUE exact duplicate; derive SlotGrid não publicado por precedência; `PublishDay` continua gap-fill (migration `AddScheduleTemplateExactDuplicateUnique`)
- [x] Fila de espera opcional por Location (`QueueEnabled` + `QueueOpeningTime`, T diário em America/Sao_Paulo; sessão `(Tenant, Location, OpeningDate)`; ticket FIFO 90s). Default off. Migration `AddReservationWaitingQueue`. ADR [`docs/adr/0003-reservation-waiting-queue.md`](./docs/adr/0003-reservation-waiting-queue.md)
- [ ] Aplicar migration `AddReservationWaitingQueue` no Supabase/Railway (não aplicar da máquina de implementação)
- [x] Follow-up fila: isolamento tenant em DockerFact; `CompleteTurnAsync` revalida `TurnExpiresAt`; relógio na fronteira 00:00/WR e abertura perto da meia-noite. Não criar ação em `RentalAssetsController` sem `[Authorize]` (já atribuído por action; Customer policy nas rotas de fila).

## 2.7. Catálogo de famílias de Asset — FEITO (código)

Decisões: ADR [`docs/adr/0002-asset-families-jsonb.md`](./docs/adr/0002-asset-families-jsonb.md). Glossário: **Asset**, **AssetFamily**.

- [x] `AssetFamily` + `TenantAssetFamily` + `Asset.FamilyId` / `Attributes`
- [x] Seeds `spaces` / `electrical` / `goods` / `generic`
- [x] `GET /api/asset-families` (+ `/active`); create/update tenant com `AssetFamilyKeys`
- [x] `GET /api/admin/asset-families` — catálogo global para SuperAdmin (policy PlatformAdmin, sem `tenant_id`); B2B `GET /api/asset-families` permanece `RequirePermission` / fail-closed sem tenant
- [x] Validação de attributes no `AssetService`
- [x] F-16: `BulkCreate` respeita `RentalType` (Location = N entidades qty 1; Good = 1 entidade com estoque)
- [ ] Aplicar migration `AddAssetFamilies` no Supabase/Railway
- [ ] CRUD visual de famílias no Super-Admin (follow-up)

## 2.9. Meu Perfil B2C — FEITO (código)

Spec: [`docs/plans/active/2026-08-18-b2c-meu-perfil.md`](./docs/plans/active/2026-08-18-b2c-meu-perfil.md). Branch `feat/customer-profile`.

Decisões (2026-08-18): DTO próprio; PATCH só Nome + Foto; identidade (e-mail/telefone/CPF/senha) somente leitura.

- [x] `GET /api/customers/me` + `PATCH /api/customers/me` (`Authorize(Policy = "Customer")`; `customerId` só do JWT)
- [x] UI portal `/app/perfil` + menu da conta (repo `vlr-web`, mesma branch)
- FOLLOW_UP (fora deste MVP): alteração de e-mail com verificação; telefone com SMS; CPF; senha B2C (troca/recuperação); CEP/endereço; ExtraAttributes. Upload de foto **não** aberto — o fluxo de cadastro (`fileToCompressedDataUrl`) foi reutilizado.

## 3. Notificações reais (Resend + WhatsApp) — ADIADA

- [x] Providers Resend / Meta / Dev + webhook WhatsApp.
- [x] F-05: gate `Notifications:AllowExternalDelivery` (bool?). **v1 Catalog (2026-08-28):** unset/null → false in **every** environment (including Production host names). Explicit `true` required for Resend/Meta.
- [x] Gates por canal: `AllowExternalEmail` / `AllowExternalWhatsApp` (override do global). Unset continua fail-closed. SMS Catalog permanece Dev.
- [ ] **Ops (humano):** no Railway **production**, setar `Notifications__AllowExternalEmail=true` + Resend + `App__FrontendBaseUrl`. Storage reuses existing `Supabase__Url` / `Supabase__ServiceRoleKey` (do not duplicate `Storage__*` secrets). Código LogError se Dev permanecer; processo sobe.
- [ ] Config externa Meta no Railway + template Authentication.
- [x] Provider SMS real quando sair do Dev — **somente verificação de celular B2C via Twilio Verify** (sync `IPhoneVerificationClient`). Catalog SMS (`ISmsProvider` / `DevSmsProvider`) continua Dev.
- [x] Cadastro pending (`PhoneVerifiedAt` null) com o mesmo e-mail + telefone + documento **retoma** a linha; falha Twilio **não apaga** o Customer; DTO `verificationStarted`.
- [x] `resend-verification` devolve 202 se o e-mail não existir, já estiver verificado, sem telefone, ou em cooldown 45s (anti-enumeração).
- [x] Rate limit de aplicação: cooldown 45s por tenant+e-mail após start OK; 10 tentativas / 10 min por IP (429 no resend; register não 429).
- Local sem Twilio: register devolve 200 + `verificationStarted: false`; verify-phone continua 503 fail-closed.

## 4. Enforcement de módulos por tenant

`core.tenant_modules` existe; menu B2C já filtra por ativos. Falta middleware/filtro API → 403.

## 5. Fluxo de convite B2B real — EM ANDAMENTO

- [x] Tabela `core.user_invites` + migration `AddUserInvites`
- [x] `POST /api/admin/tenants/{id}/invites` + list users + promote + resend/revoke
- [x] `POST /api/invites/accept` (anonymous) → Supabase user + `User` + role (`UserInviteRole` quando houver, senão `RoleName`)
- [x] `GET/DELETE /api/admin/users` (lista global Super-Admin + exclusão; índices `full_name` / `tenant_id`)
- [x] Membership Super-Admin por tenant (`User` único em TenantId+SupabaseAuthId) + enter/exit via app_metadata.tenant_id
- [x] Wizard Super-Admin: passo “Admin” (nome/e-mail, sem senha)
- [x] Edit tenant: seção usuários/convites
- [x] FE `/invite` chama API real
- [x] E-mail (Resend) com layout Rolvix + `App:FrontendBaseUrl` (prod nunca emite localhost)
- [x] Reset de senha B2B: `POST /api/auth/forgot-password` → `generate_link` + Resend (`RolvixEmailLayout`); FE não usa mais `resetPasswordForEmail`
- [ ] **Ops (humano):** Railway PROD deve ter `Notifications__AllowExternalEmail=true` (obrigatório para convite/recovery; não é opcional de DEV). Ver `docs/runbooks/password-recovery-resend.md`.
- [x] Tenant RBAC v1 (API): `RequirePermission`, catálogo, Roles CRUD, invite `roleIds[]`, `/me` additive. UI no `vlr-web`.
- [ ] Migrar onboarding público para invite (remover senha do admin)

## 5.1. Tenant RBAC v1 — FEITO (código API)

Spec: [`docs/plans/active/2026-08-27-tenant-rbac-v1.md`](./docs/plans/active/2026-08-27-tenant-rbac-v1.md). Branch `feat/tenant-rbac-v1`.

- [x] 37 permissions + Admin wildcard / User bundle; Technician execute bundle se a role existir
- [x] `GET /api/roles`, `GET /api/permissions`, mutações de roles, `PUT /api/users/{id}/roles`, `GET /api/users`
- [x] Stub `POST /api/users/invite` substituído (`roleIds` 1..N); `UserInvite.RoleName` legado permanece
- [x] Last-admin 409, privilege escalation 403, fail-closed; PlatformAdmin com tenant = wildcard
- [x] OS assigned-only por `os.work_orders.execute` (não pelo nome Technician)
- [ ] Aplicar migration `AddTenantRbacV1` via workflow `database-migrations` (`target=development`, `mode=list` then `apply`). Não aplicar da máquina de implementação.
- [x] UI Pessoas e acesso no `vlr-web` (merged `develop`)

## 5.2. Remote EF migrations — FEITO (código)

Runbook: [`docs/runbooks/database-migrations.md`](./docs/runbooks/database-migrations.md). Inspector: `tools/Platform.MigrationInspector`. Workflow: `.github/workflows/database-migrations.yml` (`workflow_dispatch` only).

- [x] `mode=list` (read-only) / `mode=apply` with identity preflight (DEV ref `jzptnjyzijklutinpxag`, PROD ref `kbptdzfbngelzdhriyhf`)
- [x] GitHub Environment-scoped secret `ConnectionStrings__DefaultConnection` (human creates environments + secrets in the GitHub UI)
- [ ] Human: create GitHub Environment `development` + secret, dispatch `mode=list`
- [ ] Human: production environment + required reviewers (UI) — not in this cycle

Follow-up: `REVIEW_DEV_HOSTING_ENVIRONMENT` — Railway env `development` currently sets `ASPNETCORE_ENVIRONMENT=Production`. Do not flip that without reviewing notification/host gates.

## 6. Catalog & Orders v1 — EM ANDAMENTO (código)

Spec: [`docs/plans/active/2026-08-28-catalog-orders.md`](./docs/plans/active/2026-08-28-catalog-orders.md). Branch `feat/catalog-orders`. Pack: [`docs/context-packs/active/catalog.md`](./docs/context-packs/active/catalog.md).

- [x] Module `catalog` + 6 permissions (37→43); Admin wildcard unchanged; DefaultUserKeys unchanged
- [x] CustomerType + Document (keep Cpf); CPF check digits on dynamic validator; CNPJ
- [x] IStorageProvider (public images / private technical)
- [x] Catalog domain + orders state machine + outbox notifications
- [x] `AllowExternalDelivery` unset = false
- [x] UI B2B/B2C no `vlr-web` (branch `feat/catalog-orders`)
- [ ] Aplicar migration Catalog & Orders no DEV (Human Action — **não** nesta implementação)
- [x] B2C portal: explicit 403 when `catalog` module inactive (other modules still pending generic middleware, §4)

## Dívidas técnicas conhecidas

- Fundação de testes: `tests/Platform.Api.Tests` (xUnit) cobre TrialGuard, policies B2B/Customer/PlatformAdmin, RBAC (resolver, matrix, last-admin, invite roleIds, OS assigned-only), overlap de Location, corrida de Location (Testcontainers; 2026-08-22 comprovado com Docker 29.5.3) e o gate DI de notificações (F-05).
- Consulta CPF “Receita/Serpro” ainda não plugada.
- Coluna `logo_url` obsoleta (produto usa só `logo_svg`).
- Ver [`docs/code-hygiene-findings.md`](./docs/code-hygiene-findings.md) (sweep 2026-08-04).
- `REVIEW_DEV_HOSTING_ENVIRONMENT`: Railway `development` + `ASPNETCORE_ENVIRONMENT=Production` (RBAC Client diagnostic hosted service does not run; notification gate treats the host as non-Development).

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
| 2026-08-17 | **Executado:** Configuração semanal alinhada à Agenda do dia (grade tempo × recursos por dia da semana; OpenHours visível em todas as colunas). |
| 2026-08-17 | **Executado:** `RentalAsset.RequiresDeposit` — pagamento prévio opcional por rentable; reserva sem a flag nasce `Confirmed`. |
| 2026-08-17 | **Executado:** Layout canvas — DELETE + GetDay público com `rentalAssetIds`; UI admin em Operação; B2C escolhe data+horário e vê todos os espaços (indisponíveis visíveis). |
| 2026-08-17 | **Fix Layout:** save não trava em percentuais fora do canvas; tamanho do mapa persistido; “Organizar sozinho”. |
| 2026-08-17 | **Fix escala:** `GetDay` deriva horários SlotGrid dos templates semanais (reserva B2C sem PublishDay); seed força SlotGrid; grade admin cabe o dia sem scroll interno. |
| 2026-08-18 | **Docs:** wizard de recursos (Operação + presets de preço expandindo `RentalPricing` por weekday). UI no `vlr-web`. |
| 2026-08-18 | **Docs:** fundação multi-agent (architect / implementer / reviewer), Human Decision Gate, Git Work Policy e `docs/plans`. |
| 2026-08-18 | **Docs:** GLM architect padrão; Fable só com aprovação; context-packs; agent-feedback. |
| 2026-08-18 | **Docs:** ids de subagent disambiguados no workspace multi-root (`rolvix-architect`, `api-implementer`, `api-reviewer`). |
| 2026-08-18 | **Iniciado:** Meu Perfil B2C — spec aprovada (A/A/A); `GET`/`PATCH /api/customers/me`; UI no `vlr-web`. FOLLOW_UPs de identidade/endereço/extras. |
| 2026-08-18 | **Executado (API):** `GET`/`PATCH /api/customers/me` (policy Customer; PATCH só Name + PhotoUrl; DTO separado do login). UI no `vlr-web`. |
| 2026-08-18 | **Executado (FE irmão):** Meu Perfil no portal (`/app/perfil`); review API e web sem Critical/High. |
| 2026-08-20 | **Docs:** protocolo Git multi-machine no `AGENTS.md` (Session Bootstrap, Task Checkpoint, Session Handoff). |
| 2026-08-21 | **Docs:** Autonomous Delivery + Merge Risk Gate (Fable). Parent dono do ciclo até merge em `develop`; `main`/PROD continuam Human Gate. |
| 2026-08-21 | **Fix F-12:** trial read-only também bloqueia `BookSlotAsync` e `CreateReservationAsync`. |
| 2026-08-21 | **Fix F-02:** `/hangfire` exige PlatformAdmin (email allowlist); JWT Customer/B2B comum é recusado. |
| 2026-08-21 | **Executado:** fundação xUnit (`tests/Platform.Api.Tests`) — TrialGuard, policies B2B/Customer/PlatformAdmin, overlap de Location via `GetReservedQuantityAsync` (SQLite não traduz o join+enum da reserva); extração `AddRolvixPolicies`. F-01 não resolvido. |
| 2026-08-21 | **Fix F-01:** `SELECT … FOR UPDATE` no `RentalAsset` serializa `CreateReservationAsync` / `BookSlotAsync`; prova em `ReservationConcurrencyTests` (Testcontainers PostgreSQL; skip se o named pipe/socket Docker não existir). Sem exclusion constraint e sem SERIALIZABLE. |
| 2026-08-21 | **Executado:** `POST /api/assets/pricing-bulk` aplica faixas de `RentalPricing` em lote (transação; replace ou append; caps 1000/100/10000). |
| 2026-08-21 | **Fix F-15:** duplicate AssetId rejected on CreateReservation. |
| 2026-08-22 | **Fix F-05:** `Notifications:AllowExternalDelivery` (bool? tri-state) impede Resend/Meta em Development só porque há credencial; PROD/Staging continuam externos com flag unset. |
| 2026-08-22 | **F-08 BY_DESIGN:** sessões B2B e B2C são independentes; logout de uma superfície não limpa a outra; sem `signOutAll` neste ciclo. |
| 2026-08-22 | **F-01 follow-up:** `ReservationConcurrencyTests` executado com Docker 29.5.3 — 2 passed, 0 skipped. Follow-up de prova fechado; lock inalterado. |
| 2026-08-22 | **Fix F-10:** `ApplyWeeklyRule` deixa de indexar só por StartTime; overlap entre OccupancyKinds permitido; duplicata exata rejeitada; SlotGrid não publicado deriva o vencedor por precedência. `PublishDay` inalterado (gap-fill). Follow-up F-10b: rewrite de Slots persistidos sobrepostos. |
| 2026-08-22 | **Fix F-10 review:** EntireRecurrence / restore / fallback de Slot→template usam `SourceTemplateId` ou a tupla completa (não só StartTime); converter Open→Closed na mesma janela vira 400, não overwrite. |
| 2026-08-22 | **Fix F-16:** `BulkCreate` respeita `RentalType` — Location cria N ativos (qty 1); Good cria um ativo com estoque em `TotalQuantity`. Sem conversão silenciosa Good→Location. |
| 2026-08-22 | **Executado (API):** fila de espera B2C opcional por Location — `QueueEnabled` + `QueueOpeningTime` (T diário America/Sao_Paulo); waiting room T−30 min; turno Active 90s; F-01 permanece. Migration `AddReservationWaitingQueue`. Spec `docs/plans/active/2026-08-22-reservation-waiting-queue.md`; ADR 0003. UI no `vlr-web`. |
| 2026-08-22 | **Follow-up fila (release):** `CompleteTurnAsync` revalida `TurnExpiresAt` (QUEUE_TURN_EXPIRED); isolamento tenant em Testcontainers; testes de relógio na meia-noite e abertura 00:15. |
| 2026-08-27 | **Executado (API):** Tenant RBAC v1 — Roles/Permissions enforcement, invite `roleIds[]`, `/me` additive `roles`+`permissions`, migration `AddTenantRbacV1`. Spec `docs/plans/active/2026-08-27-tenant-rbac-v1.md`. UI no `vlr-web`. |
| 2026-08-28 | **Iniciado:** Catalog & Orders v1 — spec `docs/plans/active/2026-08-28-catalog-orders.md`; PF/PJ; storage; outbox; `AllowExternalDelivery` unset=false. Branch `feat/catalog-orders`. Migration **não** aplicada. |
| 2026-08-28 | **Review-fix:** INSERT das 6 keys em `core.permissions`; `EnsureAsync` no update de tenant; testes de isolamento/RBAC. |
| 2026-08-31 | **Executado (API):** verificação de celular B2C via Twilio Verify v2 (`IPhoneVerificationClient`, sync). `core.otp_codes` deixa de ser escrito neste path. Catalog SMS (`ISmsProvider`) inalterado. Sem migration, sem PROD. Spec `docs/plans/active/2026-08-31-twilio-verify-phone.md`. Merge Risk Gate: `SAFE_WITH_FOLLOWUP` (enumeração 404 no resend; rate limit só Twilio; resend de já-verificado). |
| 2026-08-31 | **Fix:** cadastro B2C pending (`PhoneVerifiedAt` null) com e-mail+telefone+documento iguais retoma a linha (atualiza nome/senha); falha Twilio não apaga Customer; `verificationStarted` no DTO de register. Resend 202 neutro (desconhecido/já verificado/cooldown); rate limit de aplicação (45s e-mail, 10/10min IP → 429 só no resend). Fecha follow-ups Fable do Twilio Verify. Spec `docs/plans/active/2026-08-31-b2c-pending-registration.md`. |
| 2026-08-31 | **Fix:** gates `AllowExternalEmail` / `AllowExternalWhatsApp` com fallback no global `AllowExternalDelivery`. Unset continua fail-closed. SMS Catalog permanece Dev. Sem alterar Railway/PROD. |
| 2026-09-01 | **Fix:** SuperAdmin wizard Recursos — `GET /api/admin/asset-families` (PlatformAdmin, sem tenant). `GET /api/asset-families` permanece fail-closed sem `tenant_id`. Spec `docs/plans/active/2026-09-01-admin-asset-families-catalog.md`. |
| 2026-09-02 | **Fix:** hotfix silent Dev email/storage in PROD. Explicit `AllowExternalEmail=true` still required (F-05 Production default not restored). LogError when Production selects DevEmailProvider/DevStorageProvider, or email gate true with incomplete Resend (no secrets). Invite HTML polish (table/td) is not the incident cause. |
| 2026-09-02 | **Follow-up:** unify catalog storage on `Supabase:Url` / `Supabase:ServiceRoleKey`. Removed `Storage:SupabaseUrl` / `Storage:ServiceRoleKey` (no legacy fallback). Spec `docs/plans/active/2026-09-02-unify-supabase-storage-config.md`. |
| 2026-09-02 | **Fix:** Storage + Auth Admin send `sb_secret_` as `apikey` only (legacy JWT still `apikey` + Bearer). Capture Storage error bodies without credentials; map duplicate 409, file/mime 400, Invalid JWT/401/403 as upstream 502. Spec `docs/plans/active/2026-09-02-supabase-storage-secret-key-auth.md`. |
