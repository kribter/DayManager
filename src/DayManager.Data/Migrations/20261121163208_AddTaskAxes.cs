using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DayManager.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddTaskAxes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "CompletionMode",
                table: "items",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "Shape",
                table: "items",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "VerdictMode",
                table: "items",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            // ---------- 历史回填（11-21 事故的正面修复）----------
            //
            // 三列默认值 0 的含义：Shape=定日、CompletionMode=做一次、VerdictMode=判。
            // 这正是绝大多数历史记录该有的样子，所以只有一处需要纠正：
            // 有具体开始时间的是「定点」日程，不是定日待办。
            migrationBuilder.Sql(
                "UPDATE items SET Shape = 1 WHERE StartAt IS NOT NULL;");  // 1 = Timed

            // 【为什么不把任何一条回填成 Shape=2（定段 / DeadlineOnly）】
            // 真实库里 StartAt 空 + DueAt 在未来的 18 条，全部是 11-17 一次性
            // 按天摊出来的日常待办（11-22~24 各 6 条），不是「12-15 交」那种
            // 只有截止日的事。历史上没有任何字段记过创建意图，猜哪条是定段
            // 都等于重演旧版的推断。所以定段回填 0 条：常驻带直接空，
            // 以后只有 AI 明确用 dueDate 新建的记录才会挂进去。
            //
            // 【为什么这 18 条归「定日 / 做一次 / 判」而不是「每次各算」】
            // 用户说它们更像每日任务，但那一类靠 RRule 展开（有字段零逻辑，
            // 本轮留白）。回填不擅自改变它们的行为：它们仍会出现在各自那一天
            // 的清单里、仍按做一次结算。本轮只把「形态」从推断改成声明。
            //
            // 同样不回填 VerdictMode=Never：睡任务那批是逐日作息约束，
            // 需要单独一轮处理，别在迁移里顺手改用户没同意改的语义。

            migrationBuilder.CreateIndex(
                name: "IX_items_ParentTaskId",
                table: "items",
                column: "ParentTaskId");

            migrationBuilder.CreateIndex(
                name: "IX_items_Shape",
                table: "items",
                column: "Shape");

            migrationBuilder.AddForeignKey(
                name: "FK_items_items_ParentTaskId",
                table: "items",
                column: "ParentTaskId",
                principalTable: "items",
                principalColumn: "Id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_items_items_ParentTaskId",
                table: "items");

            migrationBuilder.DropIndex(
                name: "IX_items_ParentTaskId",
                table: "items");

            migrationBuilder.DropIndex(
                name: "IX_items_Shape",
                table: "items");

            migrationBuilder.DropColumn(
                name: "CompletionMode",
                table: "items");

            migrationBuilder.DropColumn(
                name: "Shape",
                table: "items");

            migrationBuilder.DropColumn(
                name: "VerdictMode",
                table: "items");
        }
    }
}
