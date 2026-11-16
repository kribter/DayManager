using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DayManager.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddWorkSegments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "work_segments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ItemId = table.Column<Guid>(type: "TEXT", nullable: false),
                    StartAt = table.Column<long>(type: "INTEGER", nullable: false),
                    EndAt = table.Column<long>(type: "INTEGER", nullable: true),
                    Version = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    DeletedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    DeviceId = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_work_segments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_work_segments_items_ItemId",
                        column: x => x.ItemId,
                        principalTable: "items",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_work_segments_ItemId",
                table: "work_segments",
                column: "ItemId");

            migrationBuilder.CreateIndex(
                name: "IX_work_segments_StartAt_EndAt",
                table: "work_segments",
                columns: new[] { "StartAt", "EndAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "work_segments");
        }
    }
}
