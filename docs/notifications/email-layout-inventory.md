# Platform transactional email inventory — 2026-11-09

Scope: local checkout based on `origin/develop` 86b45b9; sibling `vlr-web` inspected read-only. Inventory is source-based, including dormant paths and editable templates; no live sends or provider dashboard access.

## Render and delivery paths

| Email / preview | Producer / renderer | Send path | Before → after |
|---|---|---|---|
| Staff invitation: `invite-staff`, `invite-admin`, `invite-multiple-roles`, `invite-no-portal` | `Modules/Admin/Services/TenantUserAdminService` and `Modules/Users/Services/UserDirectoryService`; `RolvixEmailLayout.InviteBody`, `InviteSubject`, `Wrap` | `NotificationQueue` → `NotificationDispatcherService` → `IEmailProvider` | Shared HTML only → same content with shared styles, tenant name already loaded, derived text |
| Legacy invitation: `invite-legacy` | `Features/InviteUser/DispatchWelcomeNotificationHandler`; same invite renderer; subject `Convite Rolvix — defina sua senha` | Same queue/dispatcher | HTML only → explicit HTML + text. `InviteUserCommandHandler` currently returns false; handler retained and covered |
| Staff password recovery: `staff-recovery` | `Modules/Auth/Services/PasswordRecoveryService`; `RecoveryBody`, `Wrap`; subject `Redefinir senha — Rolvix` | Same queue/dispatcher | HTML only → HTML + derived text. Auth admin generates a link; platform sends the email |
| Customer email verification: `customer-verification` | `Modules/CustomerAuth/Services/CustomerAuthService.SendVerificationEmailAsync`; `EmailVerificationBody`, `Wrap` | Register/resend → synchronous `IEmailProvider` (existing documented exception) | HTML only → HTML + derived text; code/10-minute copy preserved |
| Customer password reset: `customer-password-reset` | `Modules/CustomerAuth/Services/PasswordResetEmailQueue`; `PasswordResetBody`, `Wrap` | Dedicated background queue → scoped `IEmailProvider` | HTML only → HTML + derived text; code/15-minute copy preserved |
| `rentals.reservation.pending_deposit` | `RentalsNotificationPublisher` → `ReservationCustomerEmail.Render` | Persisted `Notification`/`NotificationDelivery` → Hangfire → `NotificationOutboxProcessor` → `IEmailProvider` | HTML sent; existing text discarded → both existing alternatives sent |
| `rentals.reservation.confirmed` | Same reservation renderer | Same outbox | Same; subject, recipient, sender name, links preserved |
| `rentals.reservation.canceled` | Same reservation renderer | Same outbox | Same; cancellation copy preserved |
| `rentals.reservation.completed` | Same reservation renderer | Same outbox | Same |
| `rentals.reservation.reminder` | `ReservationReminderJob` → publisher → same renderer | Same outbox, existing reminder recheck retained | Same; timing/delivery semantics unchanged |
| `catalog.order.created` | `CatalogNotificationPublisher`, `CatalogNotificationTemplates.Seeds`; editable `NotificationTemplate` + payload interpolation | Same outbox | Plain body incorrectly supplied as HTML → exact plain text + encoded body inside shared HTML |
| `catalog.order.approved` | Same catalog renderer | Same outbox | Same |
| `catalog.order.ready` | Same catalog renderer | Same outbox | Same |
| `catalog.order.rejected` | Same catalog renderer, includes rejection reason | Same outbox | Same; untrusted reason safely encoded in HTML |
| `catalog.order.cancelled_by_supplier` | Same catalog renderer, includes cancellation reason | Same outbox | Same |
| `catalog.order.cancelled_by_customer` | Same catalog seeds; staff-only InApp routing today, external templates retained for completeness | Same outbox if an email delivery exists | Same; no new recipients/deliveries enabled |
| Custom/generic notification: `generic-custom` | Editable database template → outbox interpolation, or generic `NotificationMessage.Body` | Outbox or memory queue → provider | Body could be interpreted as markup → exact text + encoded shared HTML; markup is literal content |

No queue-specific email send/template exists in `ReservationQueueService`; queue state transitions were inspected and remain unchanged (no new notification behavior).

`catalog.order.preparing` is InApp-only: no Email seed and absent from `CatalogEventTypes.Notifying`. Rental Email seeds are metadata; the outbox deliberately renders the five reservation templates instead. No additional mail sender was found in `Core` or `Platform.Api` (searched `IEmailProvider`, `NotificationMessage`, `MailMessage`, `SmtpClient`, `SendGrid`, email enqueue/send and Resend). InApp/Push/WhatsApp/SMS are not email templates and are unchanged.

## Shared rendering and safety

- `RolvixEmailLayout.Wrap`: single header/footer, responsive presentation tables, Outlook conditional fixed-width wrapper and UTF-8. Inline light backgrounds/text remain the fallback. Shared dark CSS uses `prefers-color-scheme: dark` and Outlook `[data-ogsc]`: page `#121212`, card/content `#202024`, body/code `#f4f4f5`, muted `#c4c4cc`, links/actions `#a2c6e9` with action text `#121212`; black/white header/footer stay explicit. Regression assertions check both selector paths, inline fallback and at least 4.5:1 text/action contrast. `EmailStyles` owns shared inline text, button, code and muted styles; dark rules target those inline colors centrally. No external styles/fonts or SVG markup.
- `EmailContent` carries explicit HTML/text alternatives. Plain entry points encode the entire final body, preserving original text (including newlines, literal markup, links and substitutions). No HTML sniffing or arbitrary stored HTML injection.
- Only platform-owned renderers use `FromRenderedHtml`; it derives new text from already encoded HTML. Existing reservation text is passed verbatim, without conversion. Queue `TextBody` explicitly identifies platform-rendered HTML; unspecified content is plain text.
- Brand name uses already loaded company name in staff invites and `tenantName` in rental/catalog payloads. Missing name uses neutral Rolvix. Logo/color are not carried by these render/send inputs; no extra tenant query, new persisted field or inline tenant SVG is introduced.
- Subjects, links, recipients, sender mailbox, sender display-name overrides, queue/outbox routing, retries, reminder gates and external-delivery flags stay in their existing paths.

## Provider boundary

`NotificationsServiceCollectionExtensions` still selects `ResendEmailProvider` only through existing credentials + external delivery gates; otherwise `DevEmailProvider`. Resend's existing `POST emails` now includes both `html` and `text` with unchanged `from`, `to`, `subject` and error/quota handling. Dev accepts both alternatives and retains metadata-only logging. Legacy interface overloads remain for adapter compatibility; production callsites use explicit alternatives, while raw string sends are treated as plain text. No dependency/provider/configuration changes.

## Read-only web check

- `src/features/auth/passwordRecoveryService.ts`: `/api/auth/forgot-password`.
- `src/features/auth/inviteAcceptService.ts`: `/api/invites/accept`.
- `src/features/admin/services/tenantUsersService.ts`: API invite create/resend.
- `src/features/tenantPortal/services/tenantPortalService.ts`: API customer register, resend-verification, verify-email, password-reset request/confirm; reservation/catalog operations also call API services.
- `src/features/catalog/services/catalogService.ts`: API notification resend.
- Searches for Resend/mail sending and Supabase email OTP/recovery helpers found no direct web email sender/renderer. No web files changed. Provider-managed mail outside these platform paths is not asserted to be covered.

## Reproduce synthetic previews and tests

From repository root (SDK `/home/box/.dotnet/dotnet`, prepend `/home/box/.dotnet` to PATH):

```sh
dotnet test tests/Platform.Api.Tests/Platform.Api.Tests.csproj --no-restore -m:1 -nr:false -p:UseSharedCompilation=false --filter FullyQualifiedName~SharedEmailRenderTests --verbosity quiet --logger 'console;verbosity=minimal'
```

`Export_synthetic_previews` writes 20 `.html`/`.txt` pairs plus `index.html` to ignored `artifacts/email-layout/`, using production renderers and actual catalog Email seeds. All people/companies/codes/links are synthetic; domains are `example.invalid`. Open `index.html` locally; executor copies evidence to `/workspace/rolvix-synthetic-evidence/email-layout` and captures static HTML screenshots. Helpers make no HTTP request: provider tests capture serialized payload via an in-memory HTTP handler.

Run Notifications + CustomerAuth + PasswordRecovery first, then the full test project. Standard VSTest needs local sockets; if the sandbox blocks it, report that limitation. The executor runs standard VSTest and the existing preview exporter after this fix; do not create an alternative runner or bypass the sandbox. Static previews cannot certify Gmail/Outlook rendering or forced dark-mode transformations; real-client visual QA remains an executor follow-up.
