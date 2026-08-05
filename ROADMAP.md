# ROADMAP — vlr-api

Prioridade geral: beachhead **Rentals** (clube). Ver também `CONTEXT.md` e `frontend/ROADMAP.md`.

**Foco de produto agora:** **shell B2C + menu multi-item** (itens configuráveis por módulo) + agenda ligada aos itens.  
**Adiado:** fechar configuração externa Resend + WhatsApp (Meta).

## 0. Disciplina

1. Ao trabalhar neste repo: atualizar este arquivo (checklist + **Histórico** se mudou prioridade/escopo). Não apagar decisões — registre.
2. Ao encerrar uma tarefa (ou após progresso relevante), o agente deve descrever no chat o **próximo passo previsto** deste roadmap **e** do `frontend/vlr-web/ROADMAP.md`.
3. Em toda etapa concluída, descrever no chat **como testar** (passos de UI e/ou como disparar o endpoint).

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
- [ ] Admin B2B de reservas (listar/confirmar/cancelar).

## 2.6. Escala diária / Slots / Layout — EM ANDAMENTO (código backend)

Decisões: ADR `docs/adr/0001-rentals-slot-schedule.md`. Glossário em CONTEXT.

- [x] `OccupancyKind` (catálogo do tenant) + defaults open/closed/lesson
- [x] `ScheduleTemplate` + `Slot` + PublishDay + UpsertSlot + BookSlot
- [x] `SchedulePolicy` SlotGrid | OpenHours em `RentalAsset`
- [x] `RentalLayout` + items (API; canvas UI pendente)
- [x] Migration `AddRentalsScheduleAndLayouts` + SQL script
- [ ] Aplicar migration no Supabase
- [ ] Admin UI: kinds, templates, dia da escala
- [ ] B2C: escolher slot do dia (substituir hora manual)
- [ ] Canvas de Layout no admin

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
- [x] Modo suporte Super-Admin: `X-Support-Tenant-Id` → AmbientTenantContext (produto do tenant em aba separada)
- [x] Wizard Super-Admin: passo “Admin” (nome/e-mail, sem senha)
- [x] Edit tenant: seção usuários/convites
- [x] FE `/invite` chama API real
- [x] E-mail (Resend) com layout Rolvix + `App:FrontendBaseUrl` (prod nunca emite localhost)
- [ ] Migrar onboarding público para invite (remover senha do admin)

## Dívidas técnicas conhecidas

- Permissions/RolePermission sem uso.
- Hangfire dashboard auth fraco em produção.
- Sem testes automatizados.
- Consulta CPF “Receita/Serpro” ainda não plugada.
- Coluna `logo_url` obsoleta (produto usa só `logo_svg`).
- Ver `docs/code-hygiene-findings.md` (sweep 2026-08-04).

## Histórico

| Data | Mudança |
|------|---------|
| 2026-08-03 | Beachhead clube/Rentals; portal e registro dinâmico. |
| 2026-08-04 | CPF único FICC; início agenda B2C. |
| 2026-08-04 | **Executado:** `tenant_module_menu_items` + APIs públicas/admin; seed FICC. Shell B2C no frontend. |
| 2026-08-04 | **Executado:** `LogoSvg` no Tenant + validação SVG + branding API; `LogoUrl` legado zera em writes. |
| 2026-08-04 | **Iniciado:** escala SlotGrid/OpenHours, OccupancyKind, templates, slots, layouts (API); ADR 0001. |
| 2026-08-04 | **Executado:** convite admin B2B real (user_invites + accept + UI wizard/edit). |
