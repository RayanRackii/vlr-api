using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Platform.Core.Infrastructure.Persistence.Migrations;

/// <summary>
/// Billing slice B3 (Asaas). Additive only: four columns on <c>billing.tenant_subscriptions</c> for the provider
/// payment flow (<c>last_payment_at</c> keeps webhook transitions monotonic, <c>pending_plan_catalog_id</c> and
/// <c>pending_billing_cycle</c> hold a plan that starts with the next payment, <c>cancel_at_period_end</c> marks a
/// cancellation that takes effect when the paid period ends). Nullable or defaulted, so the previous API version
/// keeps working during the rollout. No row is rewritten.
/// </summary>
public partial class AddSubscriptionProviderBillingState : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<bool>(
            name: "cancel_at_period_end",
            schema: "billing",
            table: "tenant_subscriptions",
            type: "boolean",
            nullable: false,
            defaultValue: false);

        migrationBuilder.AddColumn<DateTimeOffset>(
            name: "last_payment_at",
            schema: "billing",
            table: "tenant_subscriptions",
            type: "timestamp with time zone",
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "pending_billing_cycle",
            schema: "billing",
            table: "tenant_subscriptions",
            type: "character varying(16)",
            maxLength: 16,
            nullable: true);

        migrationBuilder.AddColumn<Guid>(
            name: "pending_plan_catalog_id",
            schema: "billing",
            table: "tenant_subscriptions",
            type: "uuid",
            nullable: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "cancel_at_period_end",
            schema: "billing",
            table: "tenant_subscriptions");

        migrationBuilder.DropColumn(
            name: "last_payment_at",
            schema: "billing",
            table: "tenant_subscriptions");

        migrationBuilder.DropColumn(
            name: "pending_billing_cycle",
            schema: "billing",
            table: "tenant_subscriptions");

        migrationBuilder.DropColumn(
            name: "pending_plan_catalog_id",
            schema: "billing",
            table: "tenant_subscriptions");
    }
}
