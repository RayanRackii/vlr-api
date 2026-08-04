# ROADMAP — vlr-api

Prioridade geral: beachhead **Rentals** (clube). Ver também `CONTEXT.md` e `frontend/ROADMAP.md`.

**Foco de produto agora:** **agenda B2C** (disponibilidade + reserva + minhas reservas).  
**Adiado:** fechar configuração externa Resend + WhatsApp (Meta).

## 0. Disciplina

1. Ao trabalhar neste repo: atualizar este arquivo (checklist + **Histórico** se mudou prioridade/escopo). Não apagar decisões — registre.
2. Ao encerrar uma tarefa (ou após progresso relevante), o agente deve descrever no chat o **próximo passo previsto** deste roadmap **e** do `frontend/vlr-web/ROADMAP.md`.
3. Em toda etapa concluída, descrever no chat **como testar** (passos de UI e/ou como disparar o endpoint).

## 1. Registro dinâmico por tenant — FEITO (código)

Decisões (2026-08-03):
- Configurável por **Platform Admin** e **Admin do tenant**.
- Direto ao modelo dinâmico.
- Core obrigatório (colunas): `Name`, `Email`, `PasswordHash`, `Phone` (+ verify SMS).
- Extras: definição em `core.tenant_registration_fields` + valores em `Customer.ExtraAttributes` (JSONB).
- Índice de listagem: `(tenant_id, name)`.
- Tipos v1: `text`, `email`, `phone`, `cpf`, `cep`, `boolean`, `number`, `select`, `photo`, `date`.
- CPF: índice único `(tenant_id, cpf)` WHERE cpf IS NOT NULL; duplicados FICC removidos na migration `CleanupFiccDuplicateCpfsAndSeedRegistrationFields`.

- [x] Entidade `TenantRegistrationField` + migration `AddTenantRegistrationFields` + `ExtraAttributes` JSONB + índice nome.
- [x] `GET /api/public/tenants/{subdomain}/registration-schema`.
- [x] CRUD campos (`/api/registration-fields` tenant admin + `/api/admin/tenants/{id}/registration-fields` platform).
- [x] `POST /api/auth/customer/register` valida core + schema; grava extras no JSONB (CEP→ViaCEP se presente).
- [x] Migration limpa CPF duplicado FICC + seed cpf/cep/photo + unique index.
- [ ] Aplicar migrations no Railway/Supabase (`dotnet ef database update`).

## 2. Agenda B2C — EM ANDAMENTO

- [x] `GET /api/public/tenants/{subdomain}/rental-assets`.
- [x] `GET /api/reservations/availability` (público com tenant header).
- [x] `POST /api/reservations` (Customer JWT).
- [x] `GET /api/reservations/mine` (Customer JWT).
- [ ] Garantir assets/pricing cadastrados no tenant FICC para demo.
- [ ] Admin B2B de reservas (listar/confirmar/cancelar).

## 3. Notificações reais (Resend + WhatsApp) — ADIADA

- [x] Providers Resend / Meta / Dev + webhook WhatsApp.
- [x] `ISmsProvider` + `DevSmsProvider` + tipo `Sms` no dispatcher.
- [ ] Config externa Meta no Railway + template Authentication.
- [ ] Provider SMS real quando sair do Dev.
- [ ] Persistência de status de entrega WhatsApp.

## 4. Portal do tenant / Customer B2C

- [x] Branding Tenant + `GET .../branding`.
- [x] Customer password/CPF/CEP/foto/PhoneVerified (legado; CPF/CEP/foto migram para schema dinâmico).
- [x] register | verify-phone | login (dinâmico).
- [x] Validação branding admin hex/tagline.
- [ ] Aposentar OTP-only legado quando estável.

## 5. Enforcement de módulos por tenant

`core.tenant_modules` existe mas nada é aplicado. Middleware/filtro → 403.

## 6. Fluxo de convite B2B real

- [ ] Tabela de tokens; `InviteUser` ainda simulado.
- [ ] Endpoint para frontend `/invite`.
- [ ] Substituir onboarding com senha do admin.

## Dívidas técnicas conhecidas

- Permissions/RolePermission sem uso.
- Hangfire dashboard auth fraco em produção.
- Sem testes automatizados.
- Consulta CPF “Receita/Serpro” ainda não plugada.

## Histórico

| Data | Mudança |
|------|---------|
| 2026-08-03 | Beachhead clube/Rentals; WA/Resend adiados; portal como foco. |
| 2026-08-03 | Portal: login e-mail+senha; SMS no celular; branding mínimo. |
| 2026-08-03 | **Executado:** branding Tenant + Customer portal APIs + ViaCEP + Dev SMS. |
| 2026-08-03 | WhatsApp webhook validado em produção. |
| 2026-08-03 | Fix Serilog Console duplicado. |
| 2026-08-03 | Branding admin: validação hex/tagline; UI no frontend. |
| 2026-08-03 | **Prioridade:** registro dinâmico (core + JSONB extras + registration_fields). Agenda adiada. |
| 2026-08-03 | **Executado:** registration fields + register dinâmico + migration. |
| 2026-08-04 | CPF único por tenant; limpeza duplicados FICC + seed campos; início agenda B2C (assets públicos + mine). |
