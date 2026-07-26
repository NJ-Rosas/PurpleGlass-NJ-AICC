using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PurpleGlass.Modules.CallManagement.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddTelephonyProviderBoundary : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_call_sessions_TenantId_ProviderCallId",
                schema: "call_management",
                table: "call_sessions");

            migrationBuilder.AddColumn<string>(
                name: "Provider",
                schema: "call_management",
                table: "call_sessions",
                type: "character varying(50)",
                maxLength: 50,
                nullable: false,
                defaultValue: "Synthetic");

            migrationBuilder.AddColumn<string>(
                name: "ProviderParentCallId",
                schema: "call_management",
                table: "call_sessions",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "telephony_numbers",
                schema: "call_management",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    LocationId = table.Column<Guid>(type: "uuid", nullable: true),
                    Provider = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    NormalizedNumber = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    ProviderNumberId = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    InboundEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    OutboundEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_telephony_numbers", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "telephony_operations",
                schema: "call_management",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    LocationId = table.Column<Guid>(type: "uuid", nullable: false),
                    CallId = table.Column<Guid>(type: "uuid", nullable: false),
                    Type = table.Column<int>(type: "integer", nullable: false),
                    State = table.Column<int>(type: "integer", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    DispatchStartedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CompletedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    SafeErrorCode = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_telephony_operations", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "telephony_webhook_receipts",
                schema: "call_management",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Provider = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    EventId = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    CallId = table.Column<Guid>(type: "uuid", nullable: false),
                    ReceivedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_telephony_webhook_receipts", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_call_sessions_Provider_ProviderCallId",
                schema: "call_management",
                table: "call_sessions",
                columns: new[] { "Provider", "ProviderCallId" },
                unique: true,
                filter: "\"ProviderCallId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_telephony_numbers_Provider_NormalizedNumber",
                schema: "call_management",
                table: "telephony_numbers",
                columns: new[] { "Provider", "NormalizedNumber" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_telephony_numbers_TenantId_LocationId",
                schema: "call_management",
                table: "telephony_numbers",
                columns: new[] { "TenantId", "LocationId" },
                unique: true,
                filter: "\"OutboundEnabled\" AND \"IsActive\" AND \"LocationId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_telephony_operations_State_CreatedAtUtc",
                schema: "call_management",
                table: "telephony_operations",
                columns: new[] { "State", "CreatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_telephony_operations_TenantId_CallId_Type",
                schema: "call_management",
                table: "telephony_operations",
                columns: new[] { "TenantId", "CallId", "Type" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_telephony_webhook_receipts_Provider_EventId",
                schema: "call_management",
                table: "telephony_webhook_receipts",
                columns: new[] { "Provider", "EventId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "telephony_numbers",
                schema: "call_management");

            migrationBuilder.DropTable(
                name: "telephony_operations",
                schema: "call_management");

            migrationBuilder.DropTable(
                name: "telephony_webhook_receipts",
                schema: "call_management");

            migrationBuilder.DropIndex(
                name: "IX_call_sessions_Provider_ProviderCallId",
                schema: "call_management",
                table: "call_sessions");

            migrationBuilder.DropColumn(
                name: "Provider",
                schema: "call_management",
                table: "call_sessions");

            migrationBuilder.DropColumn(
                name: "ProviderParentCallId",
                schema: "call_management",
                table: "call_sessions");

            migrationBuilder.CreateIndex(
                name: "IX_call_sessions_TenantId_ProviderCallId",
                schema: "call_management",
                table: "call_sessions",
                columns: new[] { "TenantId", "ProviderCallId" },
                unique: true,
                filter: "\"ProviderCallId\" IS NOT NULL");
        }
    }
}
