using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Security.Data.Database.Migrations
{
    /// <inheritdoc />
    public partial class Phase3SessionAndSnapshot : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "SessionState",
                table: "SecurityEvents",
                type: "TEXT",
                maxLength: 24,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SnapshotPath",
                table: "SecurityEvents",
                type: "TEXT",
                maxLength: 260,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "SessionState",
                table: "SecurityEvents");

            migrationBuilder.DropColumn(
                name: "SnapshotPath",
                table: "SecurityEvents");
        }
    }
}
