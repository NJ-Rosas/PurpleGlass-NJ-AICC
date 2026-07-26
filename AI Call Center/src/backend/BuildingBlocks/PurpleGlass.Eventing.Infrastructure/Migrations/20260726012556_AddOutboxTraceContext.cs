using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PurpleGlass.Eventing.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddOutboxTraceContext : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "TraceParent",
                schema: "eventing",
                table: "outbox_messages",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TraceState",
                schema: "eventing",
                table: "outbox_messages",
                type: "character varying(512)",
                maxLength: 512,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "TraceParent",
                schema: "eventing",
                table: "outbox_messages");

            migrationBuilder.DropColumn(
                name: "TraceState",
                schema: "eventing",
                table: "outbox_messages");
        }
    }
}
