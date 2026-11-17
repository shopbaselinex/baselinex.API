using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BaselineX.api.Migrations
{
    /// <inheritdoc />
    public partial class RemoveProfileBioCityNeighborhood : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_user_profiles_city_state",
                table: "user_profiles");

            migrationBuilder.DropColumn(
                name: "bio",
                table: "user_profiles");

            migrationBuilder.DropColumn(
                name: "city",
                table: "user_profiles");

            migrationBuilder.DropColumn(
                name: "neighborhood",
                table: "user_profiles");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "bio",
                table: "user_profiles",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "city",
                table: "user_profiles",
                type: "character varying(120)",
                maxLength: 120,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "neighborhood",
                table: "user_profiles",
                type: "character varying(120)",
                maxLength: 120,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_user_profiles_city_state",
                table: "user_profiles",
                columns: new[] { "city", "state" });
        }
    }
}
