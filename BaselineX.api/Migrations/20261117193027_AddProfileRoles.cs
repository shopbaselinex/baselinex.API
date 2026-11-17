using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BaselineX.api.Migrations
{
    /// <inheritdoc />
    public partial class AddProfileRoles : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string[]>(
                name: "roles",
                table: "user_profiles",
                type: "character varying(30)[]",
                nullable: false,
                defaultValue: new string[0]);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "roles",
                table: "user_profiles");
        }
    }
}
