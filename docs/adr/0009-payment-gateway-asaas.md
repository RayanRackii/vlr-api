# Online payments go through Asaas subaccounts, one per tenant

Rolvix has no payment code. A deposit counts as paid when staff click Confirm, `Reservation.DepositPaid` is always 0, and `DepositPercent` is display only. Tenants want their customers to pay online.

**Status:** accepted (2026-11-15). The owner approved the gateway study and its defaults. The cost table and sources are in `docs/plans/active/2026-11-15-payments-asaas.md`.

## Decision

1. **Provider.** Asaas is the first provider, behind `IPaymentGateway`. Nothing outside `Modules/Payments` names Asaas, so a second adapter (Mercado Pago with OAuth and `application_fee`) can be added later without touching reservations or orders.
2. **Money flow.** Each tenant gets its own Asaas subaccount, created by Rolvix through the API from the tenant profile (`Tenant.TaxId`, name, e-mail, address). Charges are created with that subaccount's API key, so the tenant is the merchant and the money lands in the tenant's balance. Rolvix never holds or moves customer money.
3. **Platform fee.** A charge may carry an Asaas `split` to Rolvix's own wallet (`Payments:Asaas:PlatformWalletId`). The fee is a per-tenant setting and starts at 0%. With 0% no split is sent.
4. **Secrets.**
   - The root Asaas key is a Fly secret (`Payments__Asaas__RootApiKey`). It is used only to create subaccounts.
   - Each subaccount's API key is stored encrypted with ASP.NET Data Protection, with its key ring persisted in the database (`PersistKeysToDbContext`).
   - The key is never returned by an endpoint, never logged and never sent to the web.
5. **Confirmation by webhook, not by redirect.**
   - A charge becomes Paid only when Asaas posts `PAYMENT_RECEIVED` or `PAYMENT_CONFIRMED` to `POST api/webhooks/payments/asaas`.
   - The request must carry the `asaas-access-token` header. Its value is the per-subaccount token Rolvix set when it registered the webhook, and it is compared in constant time against the stored hash.
   - Each event id is stored once (unique index). A repeated event is answered 200 and not processed again.
   - Processing runs in a Hangfire job inside `PlatformScope` for the charge's tenant. The browser's polling only reads state and never changes it.
6. **Deposit amount.**
   - The deposit is now charged: `round(TotalAmount × percent / 100, 2)`.
   - The percent is resolved in this order:
     1. `RentalAsset.DepositPercent`;
     2. the effective `BookingPolicy.DepositPercent` (category, then tenant);
     3. `RentalPricing.DepositPercentage`;
     4. 100.
   - A percent of 0, or a total of 0, means no online charge.
   - The `BookingPolicy.DepositPercent` XML doc changes from "display only" to this rule.
7. **Paid deposit confirms the reservation.**
   - On a Paid deposit charge, `DepositPaid` is set to the charge amount, and the reservation goes PendingDeposit → Confirmed through the same transition as `ReservationService.ConfirmAsync`, so `ReservationConfirmed` goes out through the existing outbox.
   - If the reservation was already Confirmed, Canceled or Completed, the charge is still recorded as Paid and the reservation is not changed. Staff see "pago" on it.
8. **Expiry.**
   - A Pix charge is due 30 minutes after it is created.
   - A recurring job marks Pending charges past `DueAt` as Expired.
   - The reservation stays PendingDeposit, exactly as today. The customer can ask for a new Pix and staff can still confirm by hand.
   - Releasing the slot automatically is a separate decision.
9. **Manual path stays.** Manual Confirm keeps working for cash or payment at the counter. It does not invent a charge.
10. **Gate.**
    - Online payment exists for a tenant only when its `TenantPaymentAccount.Status` is Active.
    - Without an active account, every flow behaves exactly as it does today.
    - Webhooks stay outside tenant permissions (infra), as in the RBAC plan.

## Considered options

- **Mercado Pago first.** Its Pix fee is 0,99%, which is cheaper than R$1,99 below about R$200. However, every tenant must already have an MP account and connect it through OAuth, card payouts at 3,98% take 30 days, and there is no Rolvix-controlled onboarding. It stays the likely second adapter.
- **Pagar.me.** Split is for PSP clients only. Rejected for a SaaS.
- **Stripe Connect.** Pix is invite-only for Brazilian businesses and Express accounts are deprecated in Brazil. Rejected for now.
- **Efí.** Pix split works only between Efí accounts, with a maximum of 20. Rejected.
- **One Rolvix account that receives everything and pays tenants.** Rolvix would hold customer money, which brings regulatory exposure (it would act as a payment institution) and reconciliation work. Rejected.

## Limitations

- **Fees and payouts.** The flat Pix fee costs more than a percentage on small tickets until a volume price is negotiated with Asaas. Card money is paid out in about 32 days unless the tenant anticipates it, and Asaas charges the tenant for that.
- **Onboarding.** A subaccount must pass Asaas KYC before it can withdraw. Status Pending covers that wait.
- **Refunds.** No refunds until the refund slice. Cancelling a paid reservation does not return money. Staff refund in the Asaas panel and the reservation shows "pago".
- **Data Protection keys.** If the key ring is lost, stored subaccount keys cannot be decrypted. The keys live in the main database and follow its backup.
- **Polling.** The web polls the charge status. There is no push.

## Implementation

`Modules/Payments`: `IPaymentGateway`, `AsaasGateway`, `TenantPaymentAccount`, `PaymentCharge`, `PaymentWebhookEvent`, `AsaasWebhookController`, `PaymentEventProcessor`, `PaymentChargeExpiryJob`. Slices in `docs/plans/active/2026-11-15-payments-asaas.md`.
