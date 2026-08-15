using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PurpleGlass.Modules.Scheduling.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class InitialScheduling : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "scheduling");

            migrationBuilder.CreateTable(
                name: "appointment_projections",
                schema: "scheduling",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    LocationId = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkflowId = table.Column<Guid>(type: "uuid", nullable: false),
                    ProtectedExternalReference = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    StartUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    EndUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    OfficeTimeZone = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    SourceVersion = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    AuthoritativeAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_appointment_projections", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "availability_offers",
                schema: "scheduling",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    LocationId = table.Column<Guid>(type: "uuid", nullable: false),
                    AppointmentTypeCode = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    TokenHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    StartUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    EndUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    OfficeTimeZone = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    SourceVersion = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    ProviderSlotReference = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    ResourceReference = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ExpiresAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    State = table.Column<int>(type: "integer", nullable: false),
                    ConsumedByWorkflowId = table.Column<Guid>(type: "uuid", nullable: true),
                    ConsumedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    Version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_availability_offers", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "idempotency_receipts",
                schema: "scheduling",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    LocationId = table.Column<Guid>(type: "uuid", nullable: false),
                    Operation = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    KeyHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    SemanticFingerprint = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    WorkflowId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_idempotency_receipts", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "provider_operations",
                schema: "scheduling",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    LocationId = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkflowId = table.Column<Guid>(type: "uuid", nullable: false),
                    Kind = table.Column<int>(type: "integer", nullable: false),
                    State = table.Column<int>(type: "integer", nullable: false),
                    Attempt = table.Column<int>(type: "integer", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    NextAttemptAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    DispatchStartedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CompletedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ResultCode = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    Version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_provider_operations", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "appointment_workflows",
                schema: "scheduling",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    LocationId = table.Column<Guid>(type: "uuid", nullable: false),
                    OfferId = table.Column<Guid>(type: "uuid", nullable: false),
                    AppointmentTypeCode = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    PartyReference = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    SemanticFingerprint = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    CorrelationId = table.Column<Guid>(type: "uuid", nullable: false),
                    State = table.Column<int>(type: "integer", nullable: false),
                    ResultCode = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    ManualReviewRequired = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    LastTransitionAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_appointment_workflows", x => x.Id);
                    table.ForeignKey(
                        name: "FK_appointment_workflows_availability_offers_OfferId",
                        column: x => x.OfferId,
                        principalSchema: "scheduling",
                        principalTable: "availability_offers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_appointment_projections_TenantId_LocationId_ProtectedExtern~",
                schema: "scheduling",
                table: "appointment_projections",
                columns: new[] { "TenantId", "LocationId", "ProtectedExternalReference" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_appointment_projections_TenantId_LocationId_WorkflowId",
                schema: "scheduling",
                table: "appointment_projections",
                columns: new[] { "TenantId", "LocationId", "WorkflowId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_appointment_workflows_OfferId",
                schema: "scheduling",
                table: "appointment_workflows",
                column: "OfferId");

            migrationBuilder.CreateIndex(
                name: "IX_appointment_workflows_State_LastTransitionAtUtc",
                schema: "scheduling",
                table: "appointment_workflows",
                columns: new[] { "State", "LastTransitionAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_appointment_workflows_TenantId_LocationId_Id",
                schema: "scheduling",
                table: "appointment_workflows",
                columns: new[] { "TenantId", "LocationId", "Id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_availability_offers_TenantId_LocationId_ExpiresAtUtc",
                schema: "scheduling",
                table: "availability_offers",
                columns: new[] { "TenantId", "LocationId", "ExpiresAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_availability_offers_TenantId_LocationId_TokenHash",
                schema: "scheduling",
                table: "availability_offers",
                columns: new[] { "TenantId", "LocationId", "TokenHash" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_idempotency_receipts_TenantId_LocationId_Operation_KeyHash",
                schema: "scheduling",
                table: "idempotency_receipts",
                columns: new[] { "TenantId", "LocationId", "Operation", "KeyHash" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_provider_operations_State_NextAttemptAtUtc",
                schema: "scheduling",
                table: "provider_operations",
                columns: new[] { "State", "NextAttemptAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_provider_operations_WorkflowId_Kind_Attempt",
                schema: "scheduling",
                table: "provider_operations",
                columns: new[] { "WorkflowId", "Kind", "Attempt" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "appointment_projections",
                schema: "scheduling");

            migrationBuilder.DropTable(
                name: "appointment_workflows",
                schema: "scheduling");

            migrationBuilder.DropTable(
                name: "idempotency_receipts",
                schema: "scheduling");

            migrationBuilder.DropTable(
                name: "provider_operations",
                schema: "scheduling");

            migrationBuilder.DropTable(
                name: "availability_offers",
                schema: "scheduling");
        }
    }
}
