using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PurpleGlass.Modules.Conversation.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class InitialConversation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "conversation");

            migrationBuilder.CreateTable(
                name: "conversations",
                schema: "conversation",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CallSession = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    LocationId = table.Column<Guid>(type: "uuid", nullable: false),
                    CorrelationId = table.Column<Guid>(type: "uuid", nullable: false),
                    ConfigurationVersion = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Language = table.Column<string>(type: "character varying(35)", maxLength: 35, nullable: false),
                    State = table.Column<int>(type: "integer", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    StartedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CompletedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    FailedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    SummaryText = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    SummaryCallerIntent = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    SummaryOutcome = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    SummaryFollowUpRequired = table.Column<bool>(type: "boolean", nullable: true),
                    SummaryEscalated = table.Column<bool>(type: "boolean", nullable: true),
                    SummaryGeneratedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    SummaryConfigurationVersion = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    Escalated = table.Column<bool>(type: "boolean", nullable: false),
                    EscalationReason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    Version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_conversations", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "conversation_turns",
                schema: "conversation",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ConversationId = table.Column<Guid>(type: "uuid", nullable: false),
                    Speaker = table.Column<int>(type: "integer", nullable: false),
                    SequenceNumber = table.Column<int>(type: "integer", nullable: false),
                    Text = table.Column<string>(type: "character varying(8000)", maxLength: 8000, nullable: false),
                    StartedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    EndedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    RecognitionConfidence = table.Column<decimal>(type: "numeric(5,4)", precision: 5, scale: 4, nullable: true),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    SafetyFlagged = table.Column<bool>(type: "boolean", nullable: false),
                    EscalationFlagged = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_conversation_turns", x => x.Id);
                    table.ForeignKey(
                        name: "FK_conversation_turns_conversations_ConversationId",
                        column: x => x.ConversationId,
                        principalSchema: "conversation",
                        principalTable: "conversations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_conversation_turns_ConversationId_SequenceNumber",
                schema: "conversation",
                table: "conversation_turns",
                columns: new[] { "ConversationId", "SequenceNumber" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_conversations_TenantId_CallSession",
                schema: "conversation",
                table: "conversations",
                columns: new[] { "TenantId", "CallSession" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_conversations_TenantId_LocationId_CreatedAtUtc",
                schema: "conversation",
                table: "conversations",
                columns: new[] { "TenantId", "LocationId", "CreatedAtUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "conversation_turns",
                schema: "conversation");

            migrationBuilder.DropTable(
                name: "conversations",
                schema: "conversation");
        }
    }
}
