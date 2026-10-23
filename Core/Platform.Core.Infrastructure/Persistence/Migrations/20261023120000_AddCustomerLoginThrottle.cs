using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Platform.Core.Infrastructure.Persistence.Migrations;

[DbContext(typeof(AppDbContext))]
[Migration("20261023120000_AddCustomerLoginThrottle")]
public sealed class AddCustomerLoginThrottle : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        CREATE TABLE core.customer_login_throttle (
            bucket_key text PRIMARY KEY,
            attempts integer NOT NULL CHECK (attempts > 0),
            expires_at timestamptz NOT NULL
        );
        CREATE INDEX ix_customer_login_throttle_expires_at
            ON core.customer_login_throttle (expires_at);
        """);

    protected override void Down(MigrationBuilder migrationBuilder) =>
        migrationBuilder.Sql("DROP TABLE core.customer_login_throttle;");
}
