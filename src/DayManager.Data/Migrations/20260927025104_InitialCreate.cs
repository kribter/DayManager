using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DayManager.Data.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "daily_contexts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Date = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    DayType = table.Column<int>(type: "INTEGER", nullable: false),
                    IsHoliday = table.Column<bool>(type: "INTEGER", nullable: false),
                    UserNote = table.Column<string>(type: "TEXT", nullable: true),
                    MoodScore = table.Column<int>(type: "INTEGER", nullable: true),
                    AiDailySummary = table.Column<string>(type: "TEXT", nullable: true),
                    PlannedCount = table.Column<int>(type: "INTEGER", nullable: false),
                    CompletedCount = table.Column<int>(type: "INTEGER", nullable: false),
                    CancelledCount = table.Column<int>(type: "INTEGER", nullable: false),
                    AverageStartDelayMinutes = table.Column<int>(type: "INTEGER", nullable: true),
                    FocusMinutes = table.Column<int>(type: "INTEGER", nullable: false),
                    EntertainmentMinutes = table.Column<int>(type: "INTEGER", nullable: false),
                    Version = table.Column<int>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    DeletedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    DeviceId = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_daily_contexts", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "projects",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Description = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    Color = table.Column<string>(type: "TEXT", maxLength: 16, nullable: true),
                    TargetDate = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    Version = table.Column<int>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    DeletedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    DeviceId = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_projects", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "usage_sessions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Platform = table.Column<int>(type: "INTEGER", nullable: false),
                    AppId = table.Column<string>(type: "TEXT", maxLength: 300, nullable: false),
                    AppName = table.Column<string>(type: "TEXT", maxLength: 300, nullable: true),
                    WindowTitle = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    StartAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    EndAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    DurationSeconds = table.Column<int>(type: "INTEGER", nullable: false),
                    IsEntertainment = table.Column<bool>(type: "INTEGER", nullable: false),
                    Version = table.Column<int>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    DeletedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    DeviceId = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_usage_sessions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "items",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Title = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Notes = table.Column<string>(type: "TEXT", maxLength: 4000, nullable: true),
                    StartAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    EndAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    DueAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    IsAllDay = table.Column<bool>(type: "INTEGER", nullable: false),
                    TimeZoneId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false, defaultValue: "Asia/Shanghai"),
                    DurationMinutes = table.Column<int>(type: "INTEGER", nullable: true),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    StartedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    CompletedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    EstimateMinutes = table.Column<int>(type: "INTEGER", nullable: true),
                    ActualMinutes = table.Column<int>(type: "INTEGER", nullable: true),
                    MinimalStep = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    RRule = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    ExDates = table.Column<string>(type: "TEXT", nullable: true),
                    RecurrenceParentId = table.Column<Guid>(type: "TEXT", nullable: true),
                    OriginalStart = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    ProjectId = table.Column<Guid>(type: "TEXT", nullable: true),
                    ParentTaskId = table.Column<Guid>(type: "TEXT", nullable: true),
                    Tags = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    Priority = table.Column<int>(type: "INTEGER", nullable: false),
                    Reminders = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    SortOrder = table.Column<int>(type: "INTEGER", nullable: false),
                    Version = table.Column<int>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    DeletedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    DeviceId = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_items", x => x.Id);
                    table.ForeignKey(
                        name: "FK_items_items_RecurrenceParentId",
                        column: x => x.RecurrenceParentId,
                        principalTable: "items",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_items_projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "IX_daily_contexts_Date",
                table: "daily_contexts",
                column: "Date",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_daily_contexts_UpdatedAt",
                table: "daily_contexts",
                column: "UpdatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_items_DueAt",
                table: "items",
                column: "DueAt");

            migrationBuilder.CreateIndex(
                name: "IX_items_ProjectId",
                table: "items",
                column: "ProjectId");

            migrationBuilder.CreateIndex(
                name: "IX_items_RecurrenceParentId",
                table: "items",
                column: "RecurrenceParentId");

            migrationBuilder.CreateIndex(
                name: "IX_items_StartAt_Status",
                table: "items",
                columns: new[] { "StartAt", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_items_Status",
                table: "items",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_items_UpdatedAt",
                table: "items",
                column: "UpdatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_projects_Status",
                table: "projects",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_projects_UpdatedAt",
                table: "projects",
                column: "UpdatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_usage_sessions_AppId",
                table: "usage_sessions",
                column: "AppId");

            migrationBuilder.CreateIndex(
                name: "IX_usage_sessions_IsEntertainment",
                table: "usage_sessions",
                column: "IsEntertainment");

            migrationBuilder.CreateIndex(
                name: "IX_usage_sessions_StartAt_EndAt",
                table: "usage_sessions",
                columns: new[] { "StartAt", "EndAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "daily_contexts");

            migrationBuilder.DropTable(
                name: "items");

            migrationBuilder.DropTable(
                name: "usage_sessions");

            migrationBuilder.DropTable(
                name: "projects");
        }
    }
}
