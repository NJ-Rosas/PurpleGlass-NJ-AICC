using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PurpleGlass.Eventing.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class ExpandOutboxEnvelope : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(name: "CausationId", schema: "eventing", table: "outbox_messages", type: "uuid", nullable: true);
            migrationBuilder.AddColumn<DateTimeOffset>(name: "CreatedAtUtc", schema: "eventing", table: "outbox_messages", type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP");
            migrationBuilder.AddColumn<string>(name: "DataClassification", schema: "eventing", table: "outbox_messages", type: "character varying(50)", maxLength: 50, nullable: false, defaultValue: "internal");
            migrationBuilder.AddColumn<DateTimeOffset>(name: "NextAttemptAtUtc", schema: "eventing", table: "outbox_messages", type: "timestamp with time zone", nullable: true);
            migrationBuilder.AddColumn<string>(name: "Producer", schema: "eventing", table: "outbox_messages", type: "character varying(100)", maxLength: 100, nullable: false, defaultValue: "purpleglass-platform");
            migrationBuilder.AddColumn<int>(name: "SchemaVersion", schema: "eventing", table: "outbox_messages", type: "integer", nullable: false, defaultValue: 1);
            migrationBuilder.AddColumn<string>(name: "Status", schema: "eventing", table: "outbox_messages", type: "character varying(30)", maxLength: 30, nullable: false, defaultValue: "Pending");
            migrationBuilder.AddColumn<string>(name: "TraceId", schema: "eventing", table: "outbox_messages", type: "character varying(100)", maxLength: 100, nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_outbox_messages_Status_NextAttemptAtUtc_OccurredAtUtc",
                schema: "eventing",
                table: "outbox_messages",
                columns: new[] { "Status", "NextAttemptAtUtc", "OccurredAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_outbox_messages_TenantId_OccurredAtUtc",
                schema: "eventing",
                table: "outbox_messages",
                columns: new[] { "TenantId", "OccurredAtUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(name: "IX_outbox_messages_Status_NextAttemptAtUtc_OccurredAtUtc", schema: "eventing", table: "outbox_messages");
            migrationBuilder.DropIndex(name: "IX_outbox_messages_TenantId_OccurredAtUtc", schema: "eventing", table: "outbox_messages");
            migrationBuilder.DropColumn(name: "CausationId", schema: "eventing", table: "outbox_messages");
            migrationBuilder.DropColumn(name: "CreatedAtUtc", schema: "eventing", table: "outbox_messages");
            migrationBuilder.DropColumn(name: "DataClassification", schema: "eventing", table: "outbox_messages");
            migrationBuilder.DropColumn(name: "NextAttemptAtUtc", schema: "eventing", table: "outbox_messages");
            migrationBuilder.DropColumn(name: "Producer", schema: "eventing", table: "outbox_messages");
            migrationBuilder.DropColumn(name: "SchemaVersion", schema: "eventing", table: "outbox_messages");
            migrationBuilder.DropColumn(name: "Status", schema: "eventing", table: "outbox_messages");
            migrationBuilder.DropColumn(name: "TraceId", schema: "eventing", table: "outbox_messages");
        }
    }
}
