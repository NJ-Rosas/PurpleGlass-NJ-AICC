using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PurpleGlass.Eventing.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddDeadLetterRecoveryHistory : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "DeadLetteredAtUtc",
                schema: "eventing",
                table: "outbox_messages",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "LastAttemptAtUtc",
                schema: "eventing",
                table: "outbox_messages",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "LastRecoveredAtUtc",
                schema: "eventing",
                table: "outbox_messages",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LastRecoveredBy",
                schema: "eventing",
                table: "outbox_messages",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "LastRecoveryCorrelationId",
                schema: "eventing",
                table: "outbox_messages",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "RecoveryCount",
                schema: "eventing",
                table: "outbox_messages",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.Sql("""
                UPDATE eventing.outbox_messages
                SET "LastAttemptAtUtc" = COALESCE("PublishedAtUtc", "CreatedAtUtc"),
                    "DeadLetteredAtUtc" = CASE WHEN "Status" = 'DeadLetter' THEN "CreatedAtUtc" ELSE NULL END
                WHERE "PublishedAtUtc" IS NOT NULL OR "Status" = 'DeadLetter';
                """);

            migrationBuilder.CreateIndex(
                name: "IX_outbox_messages_TenantId_Status_DeadLetteredAtUtc",
                schema: "eventing",
                table: "outbox_messages",
                columns: new[] { "TenantId", "Status", "DeadLetteredAtUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_outbox_messages_TenantId_Status_DeadLetteredAtUtc",
                schema: "eventing",
                table: "outbox_messages");

            migrationBuilder.DropColumn(
                name: "DeadLetteredAtUtc",
                schema: "eventing",
                table: "outbox_messages");

            migrationBuilder.DropColumn(
                name: "LastAttemptAtUtc",
                schema: "eventing",
                table: "outbox_messages");

            migrationBuilder.DropColumn(
                name: "LastRecoveredAtUtc",
                schema: "eventing",
                table: "outbox_messages");

            migrationBuilder.DropColumn(
                name: "LastRecoveredBy",
                schema: "eventing",
                table: "outbox_messages");

            migrationBuilder.DropColumn(
                name: "LastRecoveryCorrelationId",
                schema: "eventing",
                table: "outbox_messages");

            migrationBuilder.DropColumn(
                name: "RecoveryCount",
                schema: "eventing",
                table: "outbox_messages");
        }
    }
}
