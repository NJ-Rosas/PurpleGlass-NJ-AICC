using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PurpleGlass.Modules.Tenancy.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddDefaultCallLanguage : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "DefaultCallLanguageCode",
                schema: "tenancy",
                table: "locations",
                type: "character varying(35)",
                maxLength: 35,
                nullable: false,
                defaultValue: "en-US");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DefaultCallLanguageCode",
                schema: "tenancy",
                table: "locations");
        }
    }
}
