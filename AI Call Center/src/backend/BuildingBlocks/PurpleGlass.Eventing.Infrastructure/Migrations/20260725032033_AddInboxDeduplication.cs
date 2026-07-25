using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PurpleGlass.Eventing.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddInboxDeduplication : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "inbox_messages",
                schema: "eventing",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ConsumerName = table.Column<string>(type: "character varying(150)", maxLength: 150, nullable: false),
                    MessageId = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    ReceivedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ProcessedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_inbox_messages", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_inbox_messages_ConsumerName_MessageId",
                schema: "eventing",
                table: "inbox_messages",
                columns: new[] { "ConsumerName", "MessageId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_inbox_messages_TenantId_ReceivedAtUtc",
                schema: "eventing",
                table: "inbox_messages",
                columns: new[] { "TenantId", "ReceivedAtUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "inbox_messages",
                schema: "eventing");
        }
    }
}
