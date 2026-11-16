using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DayManager.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddDailyContextRollup : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "RolledUpAt",
                table: "daily_contexts",
                type: "INTEGER",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "RolledUpAt",
                table: "daily_contexts");
        }
    }
}
