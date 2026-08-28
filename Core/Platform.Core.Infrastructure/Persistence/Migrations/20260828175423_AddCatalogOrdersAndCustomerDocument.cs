using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Platform.Core.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddCatalogOrdersAndCustomerDocument : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "catalog");

            migrationBuilder.AddColumn<int>(
                name: "customer_type",
                schema: "core",
                table: "customers",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "document",
                schema: "core",
                table: "customers",
                type: "character varying(14)",
                maxLength: 14,
                nullable: true);

            migrationBuilder.Sql(
                "UPDATE core.customers SET document = cpf WHERE cpf IS NOT NULL AND document IS NULL;");

            migrationBuilder.CreateTable(
                name: "catalog_order_number_sequences",
                schema: "catalog",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    last_number = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_catalog_order_number_sequences", x => x.tenant_id);
                });

            migrationBuilder.CreateTable(
                name: "catalog_orders",
                schema: "catalog",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    customer_id = table.Column<Guid>(type: "uuid", nullable: false),
                    order_number = table.Column<int>(type: "integer", nullable: false),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false, defaultValue: "Requested"),
                    customer_note = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    customer_name_snapshot = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    customer_email_snapshot = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    customer_phone_snapshot = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    total_amount = table.Column<decimal>(type: "numeric(18,2)", nullable: true),
                    currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false, defaultValue: "BRL"),
                    rejected_reason = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    cancelled_reason = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    row_version = table.Column<byte[]>(type: "bytea", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_catalog_orders", x => x.id);
                    table.ForeignKey(
                        name: "fk_catalog_orders_customers_customer_id",
                        column: x => x.customer_id,
                        principalSchema: "core",
                        principalTable: "customers",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "catalog_products",
                schema: "catalog",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    code = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: true),
                    description = table.Column<string>(type: "text", nullable: true),
                    price = table.Column<decimal>(type: "numeric(18,2)", nullable: true),
                    currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false, defaultValue: "BRL"),
                    is_active = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_catalog_products", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "notification_templates",
                schema: "core",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    event_type = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    channel = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    language = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false, defaultValue: "pt-BR"),
                    subject_template = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    body_template = table.Column<string>(type: "text", nullable: false),
                    whats_app_template_name = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                    is_active = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_notification_templates", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "notifications",
                schema: "core",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    event_type = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    aggregate_type = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    aggregate_id = table.Column<Guid>(type: "uuid", nullable: false),
                    payload = table.Column<Dictionary<string, string>>(type: "jsonb", nullable: false, defaultValueSql: "'{}'::jsonb"),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_notifications", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "product_requests",
                schema: "catalog",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    customer_id = table.Column<Guid>(type: "uuid", nullable: false),
                    description = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    quantity = table.Column<int>(type: "integer", nullable: false),
                    note = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false, defaultValue: "Submitted"),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_product_requests", x => x.id);
                    table.ForeignKey(
                        name: "fk_product_requests_customers_customer_id",
                        column: x => x.customer_id,
                        principalSchema: "core",
                        principalTable: "customers",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "tenant_notification_channel_configs",
                schema: "core",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    event_type = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    channel = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_tenant_notification_channel_configs", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "catalog_order_status_history",
                schema: "catalog",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    order_id = table.Column<Guid>(type: "uuid", nullable: false),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    actor_type = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    actor_id = table.Column<Guid>(type: "uuid", nullable: true),
                    reason = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_catalog_order_status_history", x => x.id);
                    table.ForeignKey(
                        name: "fk_catalog_order_status_history_catalog_orders_order_id",
                        column: x => x.order_id,
                        principalSchema: "catalog",
                        principalTable: "catalog_orders",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "catalog_order_items",
                schema: "catalog",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    order_id = table.Column<Guid>(type: "uuid", nullable: false),
                    product_id = table.Column<Guid>(type: "uuid", nullable: false),
                    product_name_snapshot = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    product_code_snapshot = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: true),
                    unit_price_snapshot = table.Column<decimal>(type: "numeric(18,2)", nullable: true),
                    currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false, defaultValue: "BRL"),
                    quantity = table.Column<int>(type: "integer", nullable: false),
                    sub_total = table.Column<decimal>(type: "numeric(18,2)", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_catalog_order_items", x => x.id);
                    table.ForeignKey(
                        name: "fk_catalog_order_items_catalog_orders_order_id",
                        column: x => x.order_id,
                        principalSchema: "catalog",
                        principalTable: "catalog_orders",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_catalog_order_items_catalog_products_product_id",
                        column: x => x.product_id,
                        principalSchema: "catalog",
                        principalTable: "catalog_products",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "catalog_product_files",
                schema: "catalog",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    product_id = table.Column<Guid>(type: "uuid", nullable: false),
                    storage_key = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    file_name = table.Column<string>(type: "character varying(260)", maxLength: 260, nullable: false),
                    mime_type = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    size_bytes = table.Column<long>(type: "bigint", nullable: false),
                    visibility = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_catalog_product_files", x => x.id);
                    table.ForeignKey(
                        name: "fk_catalog_product_files_catalog_products_product_id",
                        column: x => x.product_id,
                        principalSchema: "catalog",
                        principalTable: "catalog_products",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "notification_deliveries",
                schema: "core",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    notification_id = table.Column<Guid>(type: "uuid", nullable: false),
                    channel = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    recipient_kind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    recipient_id = table.Column<Guid>(type: "uuid", nullable: true),
                    recipient_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    recipient_email = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    recipient_phone = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false, defaultValue: "Queued"),
                    provider_message_id = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    error_message = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    next_attempt_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    attempt_count = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_notification_deliveries", x => x.id);
                    table.ForeignKey(
                        name: "fk_notification_deliveries_notifications_notification_id",
                        column: x => x.notification_id,
                        principalSchema: "core",
                        principalTable: "notifications",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "product_request_files",
                schema: "catalog",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    product_request_id = table.Column<Guid>(type: "uuid", nullable: false),
                    storage_key = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    file_name = table.Column<string>(type: "character varying(260)", maxLength: 260, nullable: false),
                    mime_type = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    size_bytes = table.Column<long>(type: "bigint", nullable: false),
                    visibility = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false, defaultValue: "InternalB2B"),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_product_request_files", x => x.id);
                    table.ForeignKey(
                        name: "fk_product_request_files_product_requests_product_request_id",
                        column: x => x.product_request_id,
                        principalSchema: "catalog",
                        principalTable: "product_requests",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "notification_delivery_attempts",
                schema: "core",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    delivery_id = table.Column<Guid>(type: "uuid", nullable: false),
                    attempt_number = table.Column<int>(type: "integer", nullable: false),
                    started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    finished_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    outcome = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    provider_response = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    error_message = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_notification_delivery_attempts", x => x.id);
                    table.ForeignKey(
                        name: "fk_notification_delivery_attempts_notification_deliveries_deli",
                        column: x => x.delivery_id,
                        principalSchema: "core",
                        principalTable: "notification_deliveries",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_customers_tenant_id_document",
                schema: "core",
                table: "customers",
                columns: new[] { "tenant_id", "document" },
                unique: true,
                filter: "document IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_catalog_order_items_order_id",
                schema: "catalog",
                table: "catalog_order_items",
                column: "order_id");

            migrationBuilder.CreateIndex(
                name: "ix_catalog_order_items_product_id",
                schema: "catalog",
                table: "catalog_order_items",
                column: "product_id");

            migrationBuilder.CreateIndex(
                name: "ix_catalog_order_items_tenant_id_order_id",
                schema: "catalog",
                table: "catalog_order_items",
                columns: new[] { "tenant_id", "order_id" });

            migrationBuilder.CreateIndex(
                name: "ix_catalog_order_status_history_order_id",
                schema: "catalog",
                table: "catalog_order_status_history",
                column: "order_id");

            migrationBuilder.CreateIndex(
                name: "ix_catalog_order_status_history_tenant_id_order_id_created_at",
                schema: "catalog",
                table: "catalog_order_status_history",
                columns: new[] { "tenant_id", "order_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_catalog_orders_customer_id",
                schema: "catalog",
                table: "catalog_orders",
                column: "customer_id");

            migrationBuilder.CreateIndex(
                name: "ix_catalog_orders_tenant_id_created_at",
                schema: "catalog",
                table: "catalog_orders",
                columns: new[] { "tenant_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_catalog_orders_tenant_id_customer_id",
                schema: "catalog",
                table: "catalog_orders",
                columns: new[] { "tenant_id", "customer_id" });

            migrationBuilder.CreateIndex(
                name: "ix_catalog_orders_tenant_id_order_number",
                schema: "catalog",
                table: "catalog_orders",
                columns: new[] { "tenant_id", "order_number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_catalog_orders_tenant_id_status",
                schema: "catalog",
                table: "catalog_orders",
                columns: new[] { "tenant_id", "status" });

            migrationBuilder.CreateIndex(
                name: "ix_catalog_product_files_product_id",
                schema: "catalog",
                table: "catalog_product_files",
                column: "product_id");

            migrationBuilder.CreateIndex(
                name: "ix_catalog_product_files_tenant_id_product_id",
                schema: "catalog",
                table: "catalog_product_files",
                columns: new[] { "tenant_id", "product_id" });

            migrationBuilder.CreateIndex(
                name: "ix_catalog_product_files_tenant_id_storage_key",
                schema: "catalog",
                table: "catalog_product_files",
                columns: new[] { "tenant_id", "storage_key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_catalog_products_tenant_id_code",
                schema: "catalog",
                table: "catalog_products",
                columns: new[] { "tenant_id", "code" },
                unique: true,
                filter: "code IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_catalog_products_tenant_id_is_active",
                schema: "catalog",
                table: "catalog_products",
                columns: new[] { "tenant_id", "is_active" });

            migrationBuilder.CreateIndex(
                name: "ix_catalog_products_tenant_id_name",
                schema: "catalog",
                table: "catalog_products",
                columns: new[] { "tenant_id", "name" });

            migrationBuilder.CreateIndex(
                name: "ix_notification_deliveries_notification_id",
                schema: "core",
                table: "notification_deliveries",
                column: "notification_id");

            migrationBuilder.CreateIndex(
                name: "ix_notification_deliveries_tenant_id_channel",
                schema: "core",
                table: "notification_deliveries",
                columns: new[] { "tenant_id", "channel" });

            migrationBuilder.CreateIndex(
                name: "ix_notification_deliveries_tenant_id_status_next_attempt_at",
                schema: "core",
                table: "notification_deliveries",
                columns: new[] { "tenant_id", "status", "next_attempt_at" });

            migrationBuilder.CreateIndex(
                name: "ix_notification_delivery_attempts_delivery_id_attempt_number",
                schema: "core",
                table: "notification_delivery_attempts",
                columns: new[] { "delivery_id", "attempt_number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_notification_templates_event_type_channel_language",
                schema: "core",
                table: "notification_templates",
                columns: new[] { "event_type", "channel", "language" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_notifications_tenant_id_aggregate_type_aggregate_id",
                schema: "core",
                table: "notifications",
                columns: new[] { "tenant_id", "aggregate_type", "aggregate_id" });

            migrationBuilder.CreateIndex(
                name: "ix_notifications_tenant_id_created_at",
                schema: "core",
                table: "notifications",
                columns: new[] { "tenant_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_notifications_tenant_id_event_type",
                schema: "core",
                table: "notifications",
                columns: new[] { "tenant_id", "event_type" });

            migrationBuilder.CreateIndex(
                name: "ix_product_request_files_product_request_id",
                schema: "catalog",
                table: "product_request_files",
                column: "product_request_id");

            migrationBuilder.CreateIndex(
                name: "ix_product_request_files_tenant_id_product_request_id",
                schema: "catalog",
                table: "product_request_files",
                columns: new[] { "tenant_id", "product_request_id" });

            migrationBuilder.CreateIndex(
                name: "ix_product_requests_customer_id",
                schema: "catalog",
                table: "product_requests",
                column: "customer_id");

            migrationBuilder.CreateIndex(
                name: "ix_product_requests_tenant_id_created_at",
                schema: "catalog",
                table: "product_requests",
                columns: new[] { "tenant_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_product_requests_tenant_id_customer_id",
                schema: "catalog",
                table: "product_requests",
                columns: new[] { "tenant_id", "customer_id" });

            migrationBuilder.CreateIndex(
                name: "ix_tenant_notification_channel_configs_tenant_id_event_type_ch",
                schema: "core",
                table: "tenant_notification_channel_configs",
                columns: new[] { "tenant_id", "event_type", "channel" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "catalog_order_items",
                schema: "catalog");

            migrationBuilder.DropTable(
                name: "catalog_order_number_sequences",
                schema: "catalog");

            migrationBuilder.DropTable(
                name: "catalog_order_status_history",
                schema: "catalog");

            migrationBuilder.DropTable(
                name: "catalog_product_files",
                schema: "catalog");

            migrationBuilder.DropTable(
                name: "notification_delivery_attempts",
                schema: "core");

            migrationBuilder.DropTable(
                name: "notification_templates",
                schema: "core");

            migrationBuilder.DropTable(
                name: "product_request_files",
                schema: "catalog");

            migrationBuilder.DropTable(
                name: "tenant_notification_channel_configs",
                schema: "core");

            migrationBuilder.DropTable(
                name: "catalog_orders",
                schema: "catalog");

            migrationBuilder.DropTable(
                name: "catalog_products",
                schema: "catalog");

            migrationBuilder.DropTable(
                name: "notification_deliveries",
                schema: "core");

            migrationBuilder.DropTable(
                name: "product_requests",
                schema: "catalog");

            migrationBuilder.DropTable(
                name: "notifications",
                schema: "core");

            migrationBuilder.DropIndex(
                name: "ix_customers_tenant_id_document",
                schema: "core",
                table: "customers");

            migrationBuilder.DropColumn(
                name: "customer_type",
                schema: "core",
                table: "customers");

            migrationBuilder.DropColumn(
                name: "document",
                schema: "core",
                table: "customers");
        }
    }
}
