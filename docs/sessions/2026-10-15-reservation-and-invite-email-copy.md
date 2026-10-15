# 2026-10-15 — reservation and staff invite email copy (#159, #160)

Branch `fix/issue-159-160-email-content` in `vlr-api`, from `develop`. Draft PR. No migration, no deploy, no new environment variable.

Customer reservation email is rendered when the outbox sends it. It names the resource, the civil date, the start and end in the tenant zone, the total, the deposit when the reservation is still `PendingDeposit` and the pricing window has a percentage, the tenant name, and a link to `/t/{subdomain}/agenda` on the web origin `FrontendBaseUrlResolver` already returns. An empty price, deposit, tenant name, or subdomain drops that line. The From display name is the tenant (trade name, otherwise legal name). The mailbox stays `Resend:FromEmail` / the provider default name when the tenant name is missing.

`Tenant` has no timezone column. The mail uses the same fallback as the booking clock: `America/Sao_Paulo`.

Staff invite mail says "para acessar {tenant} como {role}" for any role that is not `Admin` or `SuperAdmin`. Those two keep "para administrar {tenant} no console Rolvix". Role names are loaded for the invite's own tenant. A role id from another tenant is ignored.
