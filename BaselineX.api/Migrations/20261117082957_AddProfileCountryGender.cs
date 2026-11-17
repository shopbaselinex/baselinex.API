using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BaselineX.api.Migrations
{
    /// <inheritdoc />
    public partial class AddProfileCountryGender : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "country",
                table: "user_profiles",
                type: "char(2)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "gender",
                table: "user_profiles",
                type: "character varying(30)",
                maxLength: 30,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "country",
                table: "user_profiles");

            migrationBuilder.DropColumn(
                name: "gender",
                table: "user_profiles");
        }
    }
}
