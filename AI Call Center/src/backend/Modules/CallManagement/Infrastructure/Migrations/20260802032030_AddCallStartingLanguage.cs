using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PurpleGlass.Modules.CallManagement.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddCallStartingLanguage : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "StartingLanguageCode",
                schema: "call_management",
                table: "call_sessions",
                type: "character varying(35)",
                maxLength: 35,
                nullable: false,
                defaultValue: "en-US");

            migrationBuilder.AddColumn<string>(
                name: "StartingLanguageReason",
                schema: "call_management",
                table: "call_sessions",
                type: "character varying(40)",
                maxLength: 40,
                nullable: false,
                defaultValue: "fallback");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "StartingLanguageCode",
                schema: "call_management",
                table: "call_sessions");

            migrationBuilder.DropColumn(
                name: "StartingLanguageReason",
                schema: "call_management",
                table: "call_sessions");
        }
    }
}
