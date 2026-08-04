# ROADMAP — vlr-api

Prioridade geral: beachhead **Rentals** (clube). Ver também `CONTEXT.md` e `frontend/ROADMAP.md`.

**Foco de produto agora:** **registro B2C dinâmico por tenant** (schema de campos + register).  
**Adiado:** fechar configuração externa Resend + WhatsApp (Meta).

## 0. Disciplina

1. Ao trabalhar neste repo: atualizar este arquivo (checklist + **Histórico** se mudou prioridade/escopo). Não apagar decisões — registre.
2. Ao encerrar uma tarefa (ou após progresso relevante), o agente deve descrever no chat o **próximo passo previsto** deste roadmap **e** do `frontend/vlr-web/ROADMAP.md`.
3. Em toda etapa concluída, descrever no chat **como testar** (passos de UI e/ou como disparar o endpoint).

## 1. Registro dinâmico por tenant — EM ANDAMENTO (antes da agenda)

Decisões (2026-08-03):
- Configurável por **Platform Admin** e **Admin do tenant**.
- Direto ao modelo dinâmico.
- Core obrigatório (colunas): `Name`, `Email`, `PasswordHash`, `Phone` (+ verify SMS).
- Extras: definição em `core.tenant_registration_fields` + valores em `Customer.ExtraAttributes` (JSONB).
- Índice de listagem: `(tenant_id, name)`.
- Tipos v1: `text`, `email`, `phone`, `cpf`, `cep`, `boolean`, `number`, `select`, `photo`, `date`.

- [x] Entidade `TenantRegistrationField` + migration `AddTenantRegistrationFields` + `ExtraAttributes` JSONB + índice nome.
- [x] `GET /api/public/tenants/{subdomain}/registration-schema`.
- [x] CRUD campos (`/api/registration-fields` tenant admin + `/api/admin/tenants/{id}/registration-fields` platform).
- [x] `POST /api/auth/customer/register` valida core + schema; grava extras no JSONB (CEP→ViaCEP se presente).
- [ ] Aplicar migration no Railway.
- [ ] Seed opcional FICC (cpf/cep/photo) via admin UI.

## 2. Notificações reais (Resend + WhatsApp) — ADIADA

- [x] Providers Resend / Meta / Dev + webhook WhatsApp.
- [x] `ISmsProvider` + `DevSmsProvider` + tipo `Sms` no dispatcher.
- [ ] Config externa Meta no Railway + template Authentication.
- [ ] Provider SMS real quando sair do Dev.
- [ ] Persistência de status de entrega WhatsApp.

## 3. Portal do tenant / Customer B2C

- [x] Branding Tenant + `GET .../branding`.
- [x] Customer password/CPF/CEP/foto/PhoneVerified (legado; CPF/CEP/foto migram para schema dinâmico).
- [x] register | verify-phone | login (versão fixa — a substituir pelo register dinâmico).
- [x] Validação branding admin hex/tagline.
- [ ] Aposentar OTP-only legado quando estável.
- [ ] Ciclo de vida reserva / estado das quadras (**depois** do registro dinâmico).

## 4. Enforcement de módulos por tenant

`core.tenant_modules` existe mas nada é aplicado. Middleware/filtro → 403.

## 5. Fluxo de convite B2B real

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
