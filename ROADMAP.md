# ROADMAP — vlr-api

Prioridade geral: beachhead **Rentals** (clube). Ver também `CONTEXT.md` e `frontend/ROADMAP.md`.

**Foco de produto agora:** portal B2C branded — login e-mail+senha + cadastro + SMS (em andamento / primeira fatia entregue).  
**Adiado:** fechar configuração externa Resend + WhatsApp (Meta).

## 0. Disciplina

1. Ao trabalhar neste repo: atualizar este arquivo (checklist + **Histórico** se mudou prioridade/escopo). Não apagar decisões — registre.
2. Ao encerrar uma tarefa (ou após progresso relevante), o agente deve descrever no chat o **próximo passo previsto** deste roadmap **e** do `frontend/vlr-web/ROADMAP.md`.
3. Em toda etapa concluída, descrever no chat **como testar** (passos de UI e/ou como disparar o endpoint).

## 1. Notificações reais (Resend + WhatsApp) — ETAPA PRINCIPAL DE INFRA · ADIADA

- [x] Providers Resend / Meta / Dev + webhook WhatsApp.
- [x] `ISmsProvider` + `DevSmsProvider` + tipo `Sms` no dispatcher (verificação de celular).
- [ ] Config externa Meta **retomada (2026-08-03)**: falta token permanente de System User, Phone Number ID, App Secret e Verify Token nas variáveis do Railway (`WhatsApp__*`) + webhook verificado.
- [ ] Template Meta Authentication (aprovar para OTP).
- [ ] Provider SMS real (Twilio/Zenvia/etc.) quando sair do Dev.
- [ ] Persistência de status de entrega WhatsApp.

## 2. Portal do tenant / Customer B2C — EM ANDAMENTO

- [x] Branding no Tenant: `PrimaryColor`, `AccentColor`, `WelcomeTagline`.
- [x] `GET /api/public/tenants/{subdomain}/branding`.
- [x] Customer: password hash, CPF, CEP/endereço (ViaCEP), foto, `PhoneVerifiedAt`.
- [x] Validação CPF (dígitos) + CEP (ViaCEP) no back; phone BR normalizado.
- [x] `POST /api/auth/customer/register` | `verify-phone` | `login` (e-mail+senha).
- [x] SMS de verificação enfileirado (Dev log).
- [x] Admin create/update aceita branding; validação antecipada de hex/tagline → 400 (`ValidateBrandingFields`).
- [ ] Aposentar OTP-only legado quando estável em produção.
- [ ] Ciclo de vida reserva / estado das quadras (próxima fatia).

## 3. Enforcement de módulos por tenant

`core.tenant_modules` existe mas nada é aplicado. Middleware/filtro → 403.

## 4. Fluxo de convite B2B real

- [ ] Tabela de tokens; `InviteUser` ainda simulado.
- [ ] Endpoint para frontend `/invite`.
- [ ] Substituir onboarding com senha do admin.

## Dívidas técnicas conhecidas

- Permissions/RolePermission sem uso.
- Hangfire dashboard auth fraco em produção.
- Sem testes automatizados.
- Consulta CPF “Receita/Serpro” ainda não plugada (só algoritmo + estrutura para API externa).

## Histórico

| Data | Mudança |
|------|---------|
| 2026-08-03 | Beachhead clube/Rentals; WA/Resend adiados; portal como foco. |
| 2026-08-03 | Portal: login e-mail+senha; SMS no celular; branding mínimo. |
| 2026-08-03 | **Executado:** branding Tenant + Customer portal APIs (register/verify-phone/login) + ViaCEP + Dev SMS + migrations. Frontend portal `/t/:subdomain`. |
| 2026-08-03 | WhatsApp webhook **validado em produção** (inbound messages OK). Diagnóstico: logs duplicados (Serilog Console 2x); `libgssapi_krb5` cosmético; DataProtection sem volume persistente. |
| 2026-08-03 | Fix Serilog: removido `WriteTo.Console()` duplicado em `Program.cs` (sink só via appsettings). |
| 2026-08-03 | Branding admin: validação hex/tagline no create/update; UI no frontend. Disciplina “como testar”. |
