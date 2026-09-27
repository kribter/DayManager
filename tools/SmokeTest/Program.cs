// 临时冒烟测试：验证 ItemRepository 的核心逻辑是否按预期工作。
// 刻意用一个独立的临时数据库文件，不碰用户真实的数据。

using DayManager.Core.Enums;
using DayManager.Data;
using DayManager.Data.Repositories;
using Microsoft.EntityFrameworkCore;

var dbFile = Path.Combine(Path.GetTempPath(), $"dm-smoke-{Guid.NewGuid():N}.db");
var factory = new TestFactory(dbFile);

await using (var db = factory.CreateDbContext())
{
    await db.Database.EnsureCreatedAsync();
}

var repo = new ItemRepository(factory);

var passed = 0;
var failed = 0;

void Check(string name, bool ok, string? detail = null)
{
    Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {name}{(detail is null ? "" : "  -> " + detail)}");
    if (ok) passed++; else failed++;
}

var now = DateTimeOffset.Now;
var today9 = new DateTimeOffset(now.Date.AddHours(9), TimeZoneInfo.Local.GetUtcOffset(now.Date.AddHours(9)));
var tomorrow10 = new DateTimeOffset(now.Date.AddDays(1).AddHours(10),
    TimeZoneInfo.Local.GetUtcOffset(now.Date.AddDays(1).AddHours(10)));

Console.WriteLine("=== 1. 今日清单应该包含哪些记录 ===");

var a = await repo.AddAsync("今天上午做课件", today9);
var b = await repo.AddAsync("逾期三天的报告", null, now.AddDays(-3));
var c = await repo.AddAsync("没想好什么时候做的事");
var d = await repo.AddAsync("明天上午开会", tomorrow10);

var list = await repo.GetTodayListAsync();
var titles = list.Select(x => x.Title).ToList();
Console.WriteLine("  实际清单：" + string.Join(" / ", titles));

Check("今天有时间的日程在列", titles.Contains("今天上午做课件"));
Check("逾期待办自动滚到今天", titles.Contains("逾期三天的报告"));
Check("没指定时间的待办在列", titles.Contains("没想好什么时候做的事"));
Check("明天的日程不在今天出现", !titles.Contains("明天上午开会"));
Check("无时间的新记录默认截止到今天", c.DueAt?.ToLocalTime().Date == DateTime.Today,
    $"DueAt(本地)={c.DueAt?.ToLocalTime():yyyy-MM-dd HH:mm}");

Console.WriteLine();
Console.WriteLine("=== 2. 状态流转与启动延迟 ===");

var started = await repo.SetStatusAsync(b.Id, ItemStatus.InProgress);
Check("标记为进行中会写入 StartedAt", started?.StartedAt is not null);
var firstStartedAt = started!.StartedAt;

await Task.Delay(30);
// 两个原因需要在比较前做换算：
//   1. 同一个时刻有"本地写法"和"UTC 写法"，所以要转成同一个基准再比；
//   2. 数据库里存的是毫秒，内存里的时间精确到微秒，读出来会被截断，
//      所以要比 Unix 毫秒而不是比原始值。
// 这个"毫秒精度"是数值存储方式的固有代价，对日程软件毫无影响
// ——你不需要知道某件事是在第几纳秒开始的。
var restarted = await repo.SetStatusAsync(b.Id, ItemStatus.InProgress);
Check("重复点击开始时，首次 StartedAt 不被覆盖",
    restarted?.StartedAt?.ToUnixTimeMilliseconds() == firstStartedAt?.ToUnixTimeMilliseconds(),
    $"{firstStartedAt:HH:mm:ss.fff} vs {restarted?.StartedAt:HH:mm:ss.fff}（毫秒部分应一致）");

var done = await repo.SetStatusAsync(b.Id, ItemStatus.Done);
Check("完成时写入 CompletedAt", done?.CompletedAt is not null);

var undone = await repo.SetStatusAsync(b.Id, ItemStatus.Pending);
Check("改回待办会清空 CompletedAt", undone?.CompletedAt is null);
Check("改回待办仍保留 StartedAt（曾经动过手是事实）", undone?.StartedAt is not null);

var stillThere = await repo.GetTodayListAsync();
Check("未完成的任务始终留在今天清单里", stillThere.Any(x => x.Id == b.Id));

Console.WriteLine();
Console.WriteLine("=== 3. 软删除 ===");

await repo.SoftDeleteAsync(c.Id);
var afterDelete = await repo.GetTodayListAsync();
Check("软删除后不再出现在清单中", !afterDelete.Any(x => x.Id == c.Id));

await using (var db = factory.CreateDbContext())
{
    // 关掉全局过滤，确认墓碑还在 —— 这是将来同步能感知到"删除"的关键
    var tomb = await db.Items.IgnoreQueryFilters().FirstOrDefaultAsync(x => x.Id == c.Id);
    Check("记录没有被真正删除（墓碑仍在库里）", tomb?.DeletedAt is not null);
}

Console.WriteLine();
Console.WriteLine("=== 4. 时间与元数据 ===");

await using (var db = factory.CreateDbContext())
{
    var item = await db.Items.FirstAsync(x => x.Id == a.Id);
    Check("存进去再读出来，时间点保持一致", item.StartAt.HasValue && (item.StartAt.Value - today9).TotalMilliseconds < 1,
        $"写入={today9:HH:mm:ss.fff} 读出(本地)={item.StartAt?.ToLocalTime():HH:mm:ss.fff}");
    Check("版本号随修改递增", item.Version >= 1, $"Version={item.Version}");
    Check("CreatedAt 有值", item.CreatedAt != default, $"CreatedAt(本地)={item.CreatedAt.ToLocalTime():yyyy-MM-dd HH:mm}");

    var raw = db.Database.SqlQuery<long>($"SELECT StartAt AS Value FROM items WHERE Id = {a.Id}").First();
    Console.WriteLine($"  数据库里 StartAt 的原始值 = {raw}  （= {DateTimeOffset.FromUnixTimeMilliseconds(raw).ToLocalTime():yyyy-MM-dd HH:mm}）");
}

Console.WriteLine();
Console.WriteLine($"===== 结果：通过 {passed} 项，失败 {failed} 项 =====");

try { File.Delete(dbFile); } catch { }

return failed == 0 ? 0 : 1;

internal class TestFactory(string dbFile) : IDbContextFactory<AppDbContext>
{
    public AppDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite($"Data Source={dbFile}")
            .Options;
        return new AppDbContext(options);
    }
}
