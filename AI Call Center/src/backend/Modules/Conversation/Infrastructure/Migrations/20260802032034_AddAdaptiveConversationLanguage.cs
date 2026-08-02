using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PurpleGlass.Modules.Conversation.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddAdaptiveConversationLanguage : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "LanguageChangeSequence",
                schema: "conversation",
                table: "conversations",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "LanguageChangedAtUtc",
                schema: "conversation",
                table: "conversations",
                type: "timestamp with time zone",
                nullable: false,
                defaultValue: new DateTimeOffset(new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)));

            migrationBuilder.AddColumn<decimal>(
                name: "LanguageDetectionConfidence",
                schema: "conversation",
                table: "conversations",
                type: "numeric(5,4)",
                precision: 5,
                scale: 4,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LanguageReason",
                schema: "conversation",
                table: "conversations",
                type: "character varying(40)",
                maxLength: 40,
                nullable: false,
                defaultValue: "fallback");

            migrationBuilder.AddColumn<string>(
                name: "StartingLanguage",
                schema: "conversation",
                table: "conversations",
                type: "character varying(35)",
                maxLength: 35,
                nullable: false,
                defaultValue: "en-US");

            migrationBuilder.Sql(
                "UPDATE conversation.conversations SET \"StartingLanguage\" = \"Language\", \"LanguageChangedAtUtc\" = \"CreatedAtUtc\";");

            migrationBuilder.CreateTable(
                name: "conversation_language_changes",
                schema: "conversation",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ConversationId = table.Column<Guid>(type: "uuid", nullable: false),
                    Sequence = table.Column<long>(type: "bigint", nullable: false),
                    PreviousLanguageCode = table.Column<string>(type: "character varying(35)", maxLength: 35, nullable: false),
                    LanguageCode = table.Column<string>(type: "character varying(35)", maxLength: 35, nullable: false),
                    Reason = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    ChangedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    DetectionConfidence = table.Column<decimal>(type: "numeric(5,4)", precision: 5, scale: 4, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_conversation_language_changes", x => x.Id);
                    table.ForeignKey(
                        name: "FK_conversation_language_changes_conversations_ConversationId",
                        column: x => x.ConversationId,
                        principalSchema: "conversation",
                        principalTable: "conversations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_conversation_language_changes_ConversationId_Sequence",
                schema: "conversation",
                table: "conversation_language_changes",
                columns: new[] { "ConversationId", "Sequence" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "conversation_language_changes",
                schema: "conversation");

            migrationBuilder.DropColumn(
                name: "LanguageChangeSequence",
                schema: "conversation",
                table: "conversations");

            migrationBuilder.DropColumn(
                name: "LanguageChangedAtUtc",
                schema: "conversation",
                table: "conversations");

            migrationBuilder.DropColumn(
                name: "LanguageDetectionConfidence",
                schema: "conversation",
                table: "conversations");

            migrationBuilder.DropColumn(
                name: "LanguageReason",
                schema: "conversation",
                table: "conversations");

            migrationBuilder.DropColumn(
                name: "StartingLanguage",
                schema: "conversation",
                table: "conversations");
        }
    }
}
