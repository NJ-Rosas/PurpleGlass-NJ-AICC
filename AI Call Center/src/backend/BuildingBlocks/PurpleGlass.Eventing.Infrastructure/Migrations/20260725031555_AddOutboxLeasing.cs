using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PurpleGlass.Eventing.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddOutboxLeasing : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_outbox_messages_Status_NextAttemptAtUtc_OccurredAtUtc",
                schema: "eventing",
                table: "outbox_messages");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "LeaseExpiresAtUtc",
                schema: "eventing",
                table: "outbox_messages",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "LeaseId",
                schema: "eventing",
                table: "outbox_messages",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_outbox_messages_Status_NextAttemptAtUtc_LeaseExpiresAtUtc_O~",
                schema: "eventing",
                table: "outbox_messages",
                columns: new[] { "Status", "NextAttemptAtUtc", "LeaseExpiresAtUtc", "OccurredAtUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_outbox_messages_Status_NextAttemptAtUtc_LeaseExpiresAtUtc_O~",
                schema: "eventing",
                table: "outbox_messages");

            migrationBuilder.DropColumn(
                name: "LeaseExpiresAtUtc",
                schema: "eventing",
                table: "outbox_messages");

            migrationBuilder.DropColumn(
                name: "LeaseId",
                schema: "eventing",
                table: "outbox_messages");

            migrationBuilder.CreateIndex(
                name: "IX_outbox_messages_Status_NextAttemptAtUtc_OccurredAtUtc",
                schema: "eventing",
                table: "outbox_messages",
                columns: new[] { "Status", "NextAttemptAtUtc", "OccurredAtUtc" });
        }
    }
}
