using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PurpleGlass.Modules.CallManagement.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class InitialCallManagement : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "call_management");

            migrationBuilder.CreateTable(
                name: "call_sessions",
                schema: "call_management",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    LocationId = table.Column<Guid>(type: "uuid", nullable: false),
                    Direction = table.Column<int>(type: "integer", nullable: false),
                    State = table.Column<int>(type: "integer", nullable: false),
                    ProviderCallId = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    FromNumber = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    ToNumber = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    CorrelationId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    AnsweredAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CompletedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    FailedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    Outcome = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    RecordingReference = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    RecordingRetentionEligibleAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    RecordingStorageProvider = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    RecordingContentType = table.Column<string>(type: "character varying(150)", maxLength: 150, nullable: true),
                    RecordingDurationMilliseconds = table.Column<long>(type: "bigint", nullable: true),
                    RecordedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    RecordingChecksum = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    RecordingSizeBytes = table.Column<long>(type: "bigint", nullable: true),
                    Version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_call_sessions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "outbound_request_receipts",
                schema: "call_management",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    IdempotencyKey = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    CallId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_outbound_request_receipts", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_call_sessions_TenantId_Id",
                schema: "call_management",
                table: "call_sessions",
                columns: new[] { "TenantId", "Id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_call_sessions_TenantId_LocationId_CreatedAtUtc",
                schema: "call_management",
                table: "call_sessions",
                columns: new[] { "TenantId", "LocationId", "CreatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_call_sessions_TenantId_ProviderCallId",
                schema: "call_management",
                table: "call_sessions",
                columns: new[] { "TenantId", "ProviderCallId" },
                unique: true,
                filter: "\"ProviderCallId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_outbound_request_receipts_TenantId_IdempotencyKey",
                schema: "call_management",
                table: "outbound_request_receipts",
                columns: new[] { "TenantId", "IdempotencyKey" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "call_sessions",
                schema: "call_management");

            migrationBuilder.DropTable(
                name: "outbound_request_receipts",
                schema: "call_management");
        }
    }
}
