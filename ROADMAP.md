# ROADMAP — vlr-api

Prioridade geral: deixar o módulo **Rentals** operacional para o primeiro cliente (clube: avisos sobre estado das quadras + reserva de horários).

## 1. Notificações reais (bloqueia o restante)

Providers reais implementados em `Platform.Api/Notifications/Providers/` (`Resend/`, `Meta/`, `Dev/`). A DI ativa o provider real quando as credenciais existem na config; sem credenciais, cai no provider Dev (log no console).

- [x] **E-mail (Resend):** `ResendEmailProvider` via `HttpClientFactory`, config `Resend:ApiKey/FromEmail/FromName`.
- [x] **WhatsApp (Meta Cloud API):** `MetaWhatsAppProvider` (texto + template), config `WhatsApp:GraphApiUrl/PhoneNumberId/AccessToken/VerifyToken/AppSecret`.
- [x] **Webhook WhatsApp:** `GET/POST /api/webhooks/whatsapp` — handshake `hub.verify_token`, validação de assinatura `X-Hub-Signature-256` (AppSecret), ingestão de status/mensagens com 200 imediato (`WhatsAppWebhookProcessor` hoje só loga).
- [ ] **Template de autenticação no Meta:** criar e aprovar um template categoria *Authentication* (o Meta só entrega mensagem iniciada pela empresa via template aprovado; texto livre só dentro da janela de 24h).
- [ ] **OTP B2C via WhatsApp:** `CustomerAuthService.RequestOtpAsync` ainda só loga o código; enfileirar `NotificationMessage` com `TemplateName` do template de autenticação quando aprovado.
- [ ] **Persistir status de entrega:** o webhook loga `sent/delivered/read/failed`; futuramente correlacionar com as mensagens enviadas.

## 2. Ciclo de vida da reserva (Rentals)

- [ ] Endpoints de listagem: reservas do tenant (admin B2B) e reservas do cliente (B2C).
- [ ] Confirmar reserva (registro do pagamento de depósito — `DepositPaid` hoje é sempre 0).
- [ ] Cancelar e completar reserva (com regras de prazo a definir).
- [ ] Notificar cliente nas transições (confirmação, cancelamento, lembrete).
- [ ] **Estado das quadras:** marcar `RentalAsset`/`Asset` como indisponível (manutenção/chuva) e avisar clientes com reservas afetadas.

## 3. Enforcement de módulos por tenant

`core.tenant_modules` existe mas nada é aplicado. Implementar middleware/filtro que bloqueia endpoints de módulos inativos para o tenant (retornar 403 com `{ "error": ... }`).

## 4. Fluxo de convite real

- [ ] Modelar tabela de tokens de convite; `InviteUserCommand` hoje é simulado (não persiste nada).
- [ ] Criar usuário no Supabase na ativação; endpoint que o frontend `/invite` consome.
- [ ] Substituir o onboarding público que coleta senha do admin (viola a regra de ouro).

## Dívidas técnicas conhecidas

- Permissions/RolePermission modelados sem seed nem uso na autorização.
- `PmocEngineJob` não filtra `RequiresMaintenance`.
- Hangfire dashboard em produção exige só "autenticado" (sem role PlatformAdmin).
- Sem `.sln`; READMEs vazios; sem testes automatizados.
- Rename de schemas `assets`→`inventory` e `pmoc`/`os`→`maintenance` adiado (marcadores `IInventoryModuleEntity`/`IMaintenanceModuleEntity`).
- Dois fluxos de criação de tenant (onboarding público vs admin) precisam convergir.
