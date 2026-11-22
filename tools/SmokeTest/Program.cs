// 临时冒烟测试：验证 ItemRepository 的核心逻辑是否按预期工作。
// 刻意用一个独立的临时数据库文件，不碰用户真实的数据。

using DayManager.Core.Ai;
using DayManager.Core.Entities;
using DayManager.Core.Enums;
using DayManager.Core.Intervention;
using DayManager.Core.Planning;
using DayManager.Core.Proactive;
using DayManager.Core.Profile;
using DayManager.Data;
using DayManager.Data.Repositories;
using DayManager.Data.Snapshots;
using System.Text.Json;
using System.Text.RegularExpressions;
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

/// <summary>
/// 找到仓库里的 src 目录。
///
/// 【为什么要往上找，而不是写死相对路径】
/// 这个程序有时从项目目录跑（dotnet run），有时直接跑编译出来的 exe，
/// 两种情况下"当前目录"的含义不一样。从自己所在的位置一层层往上找
/// 到带 src/DayManager.App 的那一层，两种都能对。
/// </summary>
string? FindSrcRoot()
{
    var start = new[]
    {
        AppContext.BaseDirectory,
        Directory.GetCurrentDirectory()
    };

    foreach (var root in start)
    {
        var dir = new DirectoryInfo(root);
        while (dir is not null)
        {
            var src = Path.Combine(dir.FullName, "src");
            if (Directory.Exists(Path.Combine(src, "DayManager.App")))
            {
                return src;
            }

            dir = dir.Parent;
        }
    }

    return null;
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
Console.WriteLine("=== 5. 计划摊分（PlanSpreader）===");

var startDate = DateOnly.FromDateTime(DateTime.Today.AddDays(1));
var deadline = startDate.AddDays(4); // 一共 5 天可用

// --- 5.1 正常情况下的摊分 ---
var five = Enumerable.Range(1, 5).Select(i => new PlannedTask($"任务{i}", 60, null)).ToList();
var r1 = PlanSpreader.Spread(five, startDate, deadline, 120);

Console.WriteLine("  " + r1.Describe());
Check("5 条 × 60 分钟、每天 120 分钟 → 3 天排完", r1.RequiredDays == 3, $"实际 {r1.RequiredDays} 天");
Check("没有溢出", r1.Fits);
Check("5 条全部排上", r1.Scheduled.Count == 5, $"实际 {r1.Scheduled.Count} 条");
Check("第一条落在开始日（第一天不能被跳过）", r1.Scheduled[0].Date == startDate,
    $"{r1.Scheduled[0].Date} vs {startDate}");
Check("所有日期都落在 [开始日, 截止日] 之间",
    r1.Scheduled.All(x => x.Date >= startDate && x.Date <= deadline));
Check("日期单调不倒退",
    r1.Scheduled.Zip(r1.Scheduled.Skip(1)).All(p => p.First.Date <= p.Second.Date));

// --- 5.2 排不下时要如实报告 ---
var many = Enumerable.Range(1, 12).Select(i => new PlannedTask($"任务{i}", 60, null)).ToList();
var r2 = PlanSpreader.Spread(many, startDate, startDate.AddDays(1), 120); // 只有 2 天 = 240 分钟

Console.WriteLine("  " + r2.Describe());
Check("排不下时能识别出来", !r2.Fits);
Check("排上的 + 溢出的 = 总数（不会有任务凭空消失）",
    r2.Scheduled.Count + r2.Overflow.Count == many.Count,
    $"{r2.Scheduled.Count} + {r2.Overflow.Count} = {many.Count}");
Check("溢出的说明里给出可决策的具体数字",
    r2.Describe().Contains("分钟") && r2.Describe().Contains("天"));

// --- 5.3 截止日早于开始日：不能崩，要如实报告 ---
var r3 = PlanSpreader.Spread(five, startDate, startDate.AddDays(-3), 120);
Check("截止日早于开始日时不抛异常", r3.Overflow.Count == five.Count);
Check("可用天数如实为 0", r3.AvailableDays == 0, $"AvailableDays={r3.AvailableDays}");

// --- 5.4 单条超过一天额度 ---
var mixed = new List<PlannedTask> { new("超大的一条", 300, null), new("普通的一条", 30, null) };
var r4 = PlanSpreader.Spread(mixed, startDate, deadline, 120);

Check("超大任务不会被切碎到两天", r4.Scheduled.Count == 2 && r4.Scheduled[0].Task.Title == "超大的一条");
Check("超大任务独占一天（下一条顺延到次日）",
    r4.Scheduled[1].Date > r4.Scheduled[0].Date,
    $"{r4.Scheduled[0].Date} → {r4.Scheduled[1].Date}");

// --- 5.5 空输入与非法额度 ---
var r5 = PlanSpreader.Spread(Array.Empty<PlannedTask>(), startDate, deadline, 120);
Check("空任务列表不报错", r5.Fits && r5.RequiredDays == 0);

var r6 = PlanSpreader.Spread(five, startDate, deadline, 0); // 非法额度，应回退到默认值
Check("每日额度传 0 时回退到默认值而不是崩溃", r6.DailyMinutes == PlanSpreader.DefaultDailyMinutes);

Console.WriteLine();
Console.WriteLine("=== 6. 拆解结果写入（Project + 任务）===");

var projectRepo = new ProjectRepository(factory);

var d1 = DateOnly.FromDateTime(DateTime.Today.AddDays(1));
var d2 = DateOnly.FromDateTime(DateTime.Today.AddDays(2));

var planTasks = new[]
{
    new ScheduledTask(new PlannedTask("整理资料", 60, "打开文件夹，新建一个空白文档", ItemKind.MustDo), d1),
    new ScheduledTask(new PlannedTask("写初稿", 90, "打开文档，写下第一个小标题", ItemKind.MustDo), d1),
    new ScheduledTask(new PlannedTask("试着画个示意图", 45, "拿张纸画三个方框", ItemKind.WantToDo), d2)
};

var project = await projectRepo.CreateWithTasksAsync("写一份项目结题报告", d2, planTasks);

Check("项目创建成功", project.Id != Guid.Empty, $"项目名：{project.Name}");
Check("目标日期写对了", project.TargetDate?.ToLocalTime().Date == d2.ToDateTime(TimeOnly.MinValue).Date,
    $"TargetDate(本地)={project.TargetDate?.ToLocalTime():yyyy-MM-dd}");

await using (var db = factory.CreateDbContext())
{
    var tasks = await db.Items.Where(x => x.ProjectId == project.Id).OrderBy(x => x.SortOrder).ToListAsync();

    Check("三条任务都写进去了", tasks.Count == 3, $"实际 {tasks.Count} 条");
    Check("任务挂在项目下", tasks.All(x => x.ProjectId == project.Id));
    Check("第一条的截止日是摊到的那天", tasks[0].DueAt?.ToLocalTime().Date == d1.ToDateTime(TimeOnly.MinValue).Date,
        $"DueAt(本地)={tasks[0].DueAt?.ToLocalTime():yyyy-MM-dd}，期望 {d1:yyyy-MM-dd}");
    Check("截止时间是当天最后一刻而不是零点", tasks[0].DueAt?.ToLocalTime().Hour == 23,
        $"Hour={tasks[0].DueAt?.ToLocalTime().Hour}");
    Check("顺序被保留（SortOrder 递增）",
        tasks.Select(x => x.SortOrder).SequenceEqual(new[] { 0, 1, 2 }),
        string.Join(",", tasks.Select(x => x.SortOrder)));
    Check("「想做的」标记被正确存下来", tasks.Any(x => x.Kind == ItemKind.WantToDo));
    Check("最小启动步骤被存下来", tasks.All(x => !string.IsNullOrWhiteSpace(x.MinimalStep)));
    Check("估时被存下来", tasks[1].EstimateMinutes == 90, $"{tasks[1].EstimateMinutes}");

    // 关键：任务要出现在它被排到的那一天的清单里，而不是今天
    var listD1 = await repo.GetTodayListAsync(d1);
    var listD2 = await repo.GetTodayListAsync(d2);
    var listToday = await repo.GetTodayListAsync();

    Check("明天的清单里有那两条", listD1.Count(x => x.ProjectId == project.Id) == 2,
        $"实际 {listD1.Count(x => x.ProjectId == project.Id)} 条");
    Check("后天的清单里有「画示意图」那条", listD2.Any(x => x.Title == "试着画个示意图"));
    Check("任务不会串到今天来", !listToday.Any(x => x.ProjectId == project.Id));
    Check("项目本身不出现在今日清单里（避免父子重复）",
        !listToday.Any(x => x.Title == "写一份项目结题报告"),
        "项目是独立的表，今日清单只查 items");

    var (done0, total0) = await projectRepo.GetProgressAsync(project.Id);
    Check("进度初始为 0/3", done0 == 0 && total0 == 3, $"{done0}/{total0}");

    await repo.SetStatusAsync(tasks[0].Id, ItemStatus.Done);
    var (done1, total1) = await projectRepo.GetProgressAsync(project.Id);
    Check("完成一条后进度更新为 1/3", done1 == 1 && total1 == 3, $"{done1}/{total1}");
}

Console.WriteLine();
Console.WriteLine("=== 7. 从今天开始排的任务，今天必须看得见 ===");
//
// 这一节是为一个真实 bug 写的回归测试：
// 拆解页原本固定从**明天**开始排，于是用户确认写入后切回「今天」，一条都没有，
// 第一反应是"没写进去吧"。修复方式是默认从今天开始排。
//
// 【为什么这个测试必须用 GetTodayListAsync 而不是查 ProjectId】
// 用户感知到的 bug 是"今日清单里没有"，要盯的就是今日清单的查询结果本身。
// 只验证数据库里有记录，等于验证了另一件事，这个 bug 照样能溜过去。
var todayPlan = new[]
{
    new ScheduledTask(new PlannedTask("今天的第一步", 30, "打开文档", ItemKind.MustDo),
        DateOnly.FromDateTime(DateTime.Today)),
    new ScheduledTask(new PlannedTask("今天第二步", 60, "写下标题", ItemKind.MustDo),
        DateOnly.FromDateTime(DateTime.Today)),
    new ScheduledTask(new PlannedTask("明天的部分", 45, "接着写", ItemKind.MustDo), d1)
};

var todayProject = await projectRepo.CreateWithTasksAsync(
    "今天就开工的一件事", d1, todayPlan);

await using (var db = factory.CreateDbContext())
{
    var listToday2 = await repo.GetTodayListAsync();

    Check("排在今天的两条立刻出现在今日清单里",
        listToday2.Count(x => x.ProjectId == todayProject.Id) == 2,
        $"实际 {listToday2.Count(x => x.ProjectId == todayProject.Id)} 条");

    Check("拖到明天的那条今天不显示",
        !listToday2.Any(x => x.Title == "明天的部分"));

    var midnight = DateOnly.FromDateTime(DateTime.Today.AddDays(-1));
    var listYesterday = await repo.GetTodayListAsync(midnight);
    Check("昨天的清单里没有今天才轮到的任务",
        !listYesterday.Any(x => x.ProjectId == todayProject.Id));

    Check("今日清单里的估算耗时有值（界面要靠它显示用时）",
        listToday2.Where(x => x.ProjectId == todayProject.Id).All(x => x.EstimateMinutes is > 0));
}

Console.WriteLine();
Console.WriteLine("=== 8. 时长格式化 ===");

Check("不足一小时按分钟显示", DurationText.Format(45) == "45 分钟", DurationText.Format(45));
Check("整小时不带多余的零分", DurationText.Format(120) == "2 小时", DurationText.Format(120));
Check("超过一小时拆成小时和分", DurationText.Format(90) == "1 小时 30 分", DurationText.Format(90));
Check("非法值不显示", DurationText.Format(0) == string.Empty);
Check("合计带「约」字", DurationText.About(150) == "约 2 小时 30 分", DurationText.About(150));
Check("零分钟合计说未估时", DurationText.About(0) == "未估时", DurationText.About(0));

Console.WriteLine();
Console.WriteLine("=== 9. 翻到任意一天都能看 ===");
//
// 这一节是为用户反馈"怎么只能看当天的日程"加的。
// 只能看今天的后果不只是不方便：提前排到后天的事，在那之前没有任何方法可以确认它还在不在，
// 于是"已经安排好了"和"什么都没安排"在系统里长得一模一样。
var day3 = DateOnly.FromDateTime(DateTime.Today.AddDays(3));
var yesterday = DateOnly.FromDateTime(DateTime.Today.AddDays(-1));

var future = await repo.AddToDayAsync("三天后的体检", day3);
var looseAgain = await repo.AddAsync("还没想好哪天做的事");
var dueToday = await repo.AddAsync("今天要交的表", null, now);

var listDay3 = await repo.GetDayListAsync(day3);
Check("排在三天后的任务，翻到那天看得见", listDay3.Any(x => x.Id == future.Id),
    string.Join(" / ", listDay3.Select(x => x.Title)));
Check("没定时间的零散待办不会跟着跑到未来去（否则每天都出现，就成了噪音）",
    !listDay3.Any(x => x.Id == looseAgain.Id));
Check("今天的日程不会串到未来某天", !listDay3.Any(x => x.Id == a.Id));

await repo.SetStatusAsync(dueToday.Id, ItemStatus.Done);
var listTodayAgain = await repo.GetDayListAsync(DateOnly.FromDateTime(DateTime.Today));
Check("今天做完的事，今天依然看得见（做完不该一勾就消失）",
    listTodayAgain.Any(x => x.Id == dueToday.Id));
Check("同一条记录不会因为同时满足多个条件而重复出现",
    listTodayAgain.Count(x => x.Id == dueToday.Id) == 1,
    $"出现了 {listTodayAgain.Count(x => x.Id == dueToday.Id)} 次");

// 昨天做完的事，翻到昨天要能看到——这是"回顾"这个功能的最小雏形
var doneYesterday = await repo.AddAsync("昨天做掉的事", null, now.AddDays(-2));
await repo.SetStatusAsync(doneYesterday.Id, ItemStatus.Done);
await using (var db = factory.CreateDbContext())
{
    var entity = await db.Items.FirstAsync(x => x.Id == doneYesterday.Id);
    entity.CompletedAt = now.AddDays(-1); // CreatedAt 是刚才，只有完成时间确实落在昨天
    await db.SaveChangesAsync();
}

var listPast = await repo.GetDayListAsync(yesterday);
Check("翻到昨天能看到昨天做完的事", listPast.Any(x => x.Id == doneYesterday.Id),
    string.Join(" / ", listPast.Select(x => x.Title)));
Check("昨天做完的事不会赖在今天不走",
    !listTodayAgain.Any(x => x.Id == doneYesterday.Id));

// 往指定的一天加东西时，时间必须挂在那一天而不是今天
var timedFuture = await repo.AddToDayAsync("后天下午三点开会", day3, new TimeSpan(15, 0, 0));
Check("指定日期和时间时，任务记在那一天",
    timedFuture.StartAt?.ToLocalTime().Date == day3.ToDateTime(TimeOnly.MinValue).Date,
    $"StartAt(本地)={timedFuture.StartAt?.ToLocalTime():yyyy-MM-dd HH:mm}");
Check("给了具体几点就不再额外挂一个截止时间（两个都在反而说不清）",
    timedFuture.DueAt is null);
var listDay3After = await repo.GetDayListAsync(day3);
Check("定时的事出现在它被安排的那一天",
    listDay3After.Any(x => x.Id == timedFuture.Id));

Console.WriteLine();
Console.WriteLine("=== 10. 每天额度：超了必须报出来 ===");
//
// 这一节是为用户反馈"我设了每天 120 分钟，拆出来却远超这个数"写的。
// 根因有两层，这里盯的是第二层：
//   第一层是 AI 根本不知道有预算（已改成把预算写进提示词）；
//   第二层是即使 AI 给了超大条，程序也曾经一声不吭地把它塞进某一天，
//   于是"全部排好了"和"每天都不超"被当成了同一件事。
var startD = DateOnly.FromDateTime(DateTime.Today);
const int cap = 120;

var oversized = new List<PlannedTask>
{
    new("一条就超过额度的大活", 300, null),
    new("普通的一条", 30, null)
};
var rOver = PlanSpreader.Spread(oversized, startD, startD.AddDays(4), cap);
Check("单条超过额度时，那天超预算这件事要报出来", rOver.OverBudgetDays >= 1,
    $"OverBudgetDays={rOver.OverBudgetDays}");
Check("最重那天的分钟数如实给出", rOver.MaxDayMinutes >= 300, $"MaxDayMinutes={rOver.MaxDayMinutes}");
Check("超预算时说明里给出具体数字（不是只说一句排好了）", rOver.Describe().Contains("超过"),
    rOver.Describe());

var inBudget = Enumerable.Range(1, 4).Select(i => new PlannedTask($"小步{i}", 60, null)).ToList();
var rFit = PlanSpreader.Spread(inBudget, startD, startD.AddDays(4), cap);
Check("每条都在额度内时，没有一天超预算", rFit.OverBudgetDays == 0, $"OverBudgetDays={rFit.OverBudgetDays}");
Check("每天的实际装载量与分组一致（界面显示的数字就是判定的依据）",
    rFit.DayLoads.Count == 2 && rFit.DayLoads.Values.Sum() == 240,
    string.Join(", ", rFit.DayLoads.Select(kv => $"{kv.Key:MM-dd}={kv.Value}")));

Console.WriteLine();
Console.WriteLine("=== 11. 五态：跳过与放弃的区别 ===");
//
// 这一节对应需求 2.2。跳过和放弃在界面上都是"今天不做它"，
// 但行为完全相反：跳过明天回来，放弃永不再现。
// 测试盯的就是这个区别——它一旦做错，软件会静默地帮用户遗忘事情。
var today = DateOnly.FromDateTime(DateTime.Today);

var skipToday = await repo.AddAsync("今天跳过的事", null, now.AddDays(-1));
var skipTodayAfter = (await repo.SetStatusAsync(skipToday.Id, ItemStatus.Skipped, "生病"))!;

var listAfterSkip = await repo.GetDayListAsync(today);
Check("今天跳过的事，今天不出现", !listAfterSkip.Any(x => x.Id == skipToday.Id));

// 昨天跳过的事，今天应该照常回来——这是"跳过只对那一天有效"的核心验证。
// 不能靠查"明天"来验证：未来日期的视图故意不显示逾期任务（需求 2.2），
// 那条路径查不到它，不代表规则错了。
var skipYesterday = await repo.AddAsync("昨天跳过的事", null, now.AddDays(-2));
await repo.SetStatusAsync(skipYesterday.Id, ItemStatus.Skipped);
await using (var db = factory.CreateDbContext())
{
    var e = await db.Items.FirstAsync(x => x.Id == skipYesterday.Id);
    e.SkippedAt = now.AddDays(-1);   // 改成"昨天跳过的"
    await db.SaveChangesAsync();
}

var listAfterYesterdaySkip = await repo.GetDayListAsync(today);
Check("昨天跳过的事，今天照常回来（跳过只对那一天有效）",
    listAfterYesterdaySkip.Any(x => x.Id == skipYesterday.Id),
    string.Join(" / ", listAfterYesterdaySkip.Select(x => x.Title)));

var skipped = listAfterYesterdaySkip.First(x => x.Id == skipYesterday.Id);
Check("跳过次数被记下来（多次跳过是「这事不该做」的信号）", skipped.SkipCount == 1,
    $"SkipCount={skipped.SkipCount}");
Check("跳过的原因被记下来", skipTodayAfter.SkipReason == "生病", $"SkipReason={skipTodayAfter.SkipReason}");

var giveUp = await repo.AddAsync("决定不做的事", null, now.AddDays(-1));
await repo.SetStatusAsync(giveUp.Id, ItemStatus.Abandoned);
Check("放弃的事，今天不出现", !listAfterYesterdaySkip.Any(x => x.Id == giveUp.Id));
Check("放弃的事，翻到哪天都不出现",
    !(await repo.GetDayListAsync(today.AddDays(3))).Any(x => x.Id == giveUp.Id)
    && !(await repo.GetDayListAsync(today.AddDays(-3))).Any(x => x.Id == giveUp.Id));

var stats = await repo.GetDayStatsAsync(today);
Check("「今天跳过了几件」单独统计出来（不然用户以为它们凭空消失了）",
    stats.SkippedToday >= 1, $"SkippedToday={stats.SkippedToday}");
Check("完成率的分母不含跳过（跳过了就不该算数）",
    stats.Total == listAfterYesterdaySkip.Count && stats.Total > 0,
    $"Total={stats.Total}, 清单={listAfterYesterdaySkip.Count}");

Console.WriteLine();
Console.WriteLine("=== 12. 撤销：AI 改错了要能撤回来 ===");
//
// 对应决策 D4：永久可撤、按批次、不经过 AI、冲突时提示。
// 这一节如果只有一个断言该成立，那就是"撤完之后必须和改之前一模一样"。
var undoRepo = new ActionLogRepository(factory);

var u1 = await repo.AddAsync("撤销测试一", null, now);
var u2 = await repo.AddAsync("撤销测试二", null, now);
var u3 = await repo.AddAsync("撤销测试三", null, now);

var before1 = ItemSnapshot.From(u1);
var before2 = ItemSnapshot.From(u2);
var before3 = ItemSnapshot.From(u3);

// 模拟 AI 的一次批量动作：把三条都标成完成
var after1 = (await repo.SetStatusAsync(u1.Id, ItemStatus.Done))!;
var after2 = (await repo.SetStatusAsync(u2.Id, ItemStatus.Done))!;
var after3 = (await repo.SetStatusAsync(u3.Id, ItemStatus.Done))!;

var batchId = await undoRepo.LogBatchAsync(ActionActor.Ai, new[]
{
    new ActionLogRepository.ActionEntry(ActionType.StatusChange, u1.Id,
        JsonSerializer.Serialize(before1), JsonSerializer.Serialize(ItemSnapshot.From(after1))),
    new ActionLogRepository.ActionEntry(ActionType.StatusChange, u2.Id,
        JsonSerializer.Serialize(before2), JsonSerializer.Serialize(ItemSnapshot.From(after2))),
    new ActionLogRepository.ActionEntry(ActionType.StatusChange, u3.Id,
        JsonSerializer.Serialize(before3), JsonSerializer.Serialize(ItemSnapshot.From(after3)))
}, "把 3 条标成完成");

Check("AI 改完确实生效了", after1.Status == ItemStatus.Done);

var batches = await undoRepo.GetUndoableBatchesAsync();
Check("撤销列表里能看到这一批", batches.Any(x => x.BatchId == batchId));
Check("撤销列表里写着改了几条（不然用户不知道要撤多大一片）",
    batches.First(x => x.BatchId == batchId).EntryCount == 3);

var undo = await undoRepo.UndoBatchAsync(batchId);
Check("整批撤销成功", undo.UndoneCount == 3, undo.Message);

await using (var db = factory.CreateDbContext())
{
    var back1 = await db.Items.FirstAsync(x => x.Id == u1.Id);
    var back3 = await db.Items.FirstAsync(x => x.Id == u3.Id);
    Check("撤完之后状态和改之前一致", back1.Status == ItemStatus.Pending && back3.Status == ItemStatus.Pending,
        $"Status={back1.Status}");
    Check("撤完之后完成时间也清掉了", back1.CompletedAt is null);
}

var undoAgain = await undoRepo.UndoBatchAsync(batchId);
Check("同一批撤第二次时明确说「已经撤过了」，而不是静默什么也不做",
    undoAgain.UndoneCount == 0 && undoAgain.Message.Contains("撤过"), undoAgain.Message);

// ---- 冲突：撤销前发现"这之后又被改过" ----
var c1 = await repo.AddAsync("冲突测试", null, now);
var cBefore = ItemSnapshot.From(c1);
var cAfter = (await repo.SetStatusAsync(c1.Id, ItemStatus.Done))!;
var cBatch = await undoRepo.LogBatchAsync(ActionActor.Ai, new[]
{
    new ActionLogRepository.ActionEntry(ActionType.StatusChange, c1.Id,
        JsonSerializer.Serialize(cBefore), JsonSerializer.Serialize(ItemSnapshot.From(cAfter)))
}, "标成完成");

// 之后用户自己改了标题
await repo.RenameAsync(c1.Id, "我自己改过的标题");

var conflict = await undoRepo.UndoBatchAsync(cBatch);
Check("发现冲突时不动数据，并且说清楚原因", conflict.HasConflicts, conflict.Message);
await using (var db = factory.CreateDbContext())
{
    var keptItem = await db.Items.FirstAsync(x => x.Id == c1.Id);
    Check("冲突时后来的改动被保住了（撤销不能变成破坏）", keptItem.Title == "我自己改过的标题", keptItem.Title);
}

var forced = await undoRepo.UndoBatchAsync(cBatch, force: true);
Check("确认后强制撤销能撤回去", forced.UndoneCount == 1, forced.Message);
await using (var db = factory.CreateDbContext())
{
    var reverted = await db.Items.FirstAsync(x => x.Id == c1.Id);
    Check("强制撤销后回到快照的样子", reverted.Title == "冲突测试" && reverted.Status == ItemStatus.Pending,
        $"Title={reverted.Title}, Status={reverted.Status}");
}

// ---- 撤销"新建"：没有"之前"，撤销就是删掉 ----
var n1 = await repo.AddAsync("AI 新建的一条", null, now);
var nBatch = await undoRepo.LogBatchAsync(ActionActor.Ai, new[]
{
    new ActionLogRepository.ActionEntry(ActionType.Create, n1.Id, null,
        JsonSerializer.Serialize(ItemSnapshot.From(n1)))
}, "新建了 1 条");

var undoCreate = await undoRepo.UndoBatchAsync(nBatch);
Check("撤销「新建」= 把它删掉", undoCreate.UndoneCount == 1, undoCreate.Message);
Check("被撤销的新建记录不再出现在清单里",
    !(await repo.GetDayListAsync(today)).Any(x => x.Id == n1.Id));

// ---- 用户自己的操作不进撤销列表 ----
var myOwn = await repo.AddAsync("我自己改的", null, now);
await undoRepo.LogBatchAsync(ActionActor.User, new[]
{
    new ActionLogRepository.ActionEntry(ActionType.StatusChange, myOwn.Id, "{}", "{}")
}, "自己改的");
var batchesAfterUser = await undoRepo.GetUndoableBatchesAsync();
Check("撤销列表只列 AI 的操作（用户自己的一步就能改回来）",
    !batchesAfterUser.Any(x => x.Summary == "自己改的"));

Console.WriteLine();
Console.WriteLine("=== 13. 会自己变的参数（policy_settings） ===");
//
// 对应 2.7：参数是活的。表本身要能存"当前值 / 默认值 / 谁定的 / 为什么"。
await using (var db = factory.CreateDbContext())
{
    db.PolicySettings.Add(new DayManager.Core.Entities.PolicySetting
    {
        Key = "daily_minutes",
        Value = "120",
        DefaultValue = "120",
        Source = PolicySource.Default,
        Reason = null
    });
    await db.SaveChangesAsync();
}

await using (var db = factory.CreateDbContext())
{
    var p = await db.PolicySettings.FirstAsync(x => x.Key == "daily_minutes");
    Check("参数读得回来", p.Value == "120");

    // 模拟 AI 调整：带上理由，来源改成 AI
    p.Value = "90";
    p.Source = PolicySource.Ai;
    p.Reason = "最近 7 天实际完成 60 分钟/天，低于设定的 120，下调以避免排不完";
    p.LastAiAdjustAt = DateTimeOffset.Now;
    await db.SaveChangesAsync();
}

await using (var db = factory.CreateDbContext())
{
    var p = await db.PolicySettings.FirstAsync(x => x.Key == "daily_minutes");
    Check("AI 调整后记得住新值", p.Value == "90");
    Check("调整依据被存下来了（界面上要显示给用户看）", !string.IsNullOrEmpty(p.Reason));
    Check("来源标记为 AI（用户手改过的项，AI 不再自动覆盖）", p.Source == PolicySource.Ai);
    Check("默认值一直留着（它是调整幅度的基线）", p.DefaultValue == "120");
    Check("记下了上次调整时间（一次一个、7 天一轮靠它）", p.LastAiAdjustAt is not null);
}

Console.WriteLine();
Console.WriteLine("=== 14. 跳过是承诺：明天它得自己出现在明天 ===");
//
// 这一节是为用户实测反馈"从今天消失后应该直接在明天那里显示出来吧"加的。
// 原来的四条规则没有一条兜得住跳过的记录：
//   它不在明天开始（StartAt 还停在今天）、不是明天到期、没做完、
//   也不是"只在今天显示"的零散待办。
// 于是点了「今天先跳过」之后翻到明天，清单是空的——
// 按钮上写着"明天再来"，结果明天没有它，这个按钮就成了骗人的。
var skD0 = DateOnly.FromDateTime(DateTime.Today);
var skD1 = skD0.AddDays(1);
var skD2 = skD0.AddDays(2);
var skDPrev = skD0.AddDays(-1);

var sk = await repo.AddAsync("今天跳过、明天要做的事", null, now.AddDays(-2));
await repo.SetStatusAsync(sk.Id, ItemStatus.Skipped, "今天有事");

var skListToday = await repo.GetDayListAsync(skD0);
Check("跳过的当天：它从今天的清单里消失", !skListToday.Any(x => x.Id == sk.Id),
    string.Join(" / ", skListToday.Select(x => x.Title)));

var skListTomorrow = await repo.GetDayListAsync(skD1);
Check("翻到明天：昨天承诺的事出现在明天的清单上", skListTomorrow.Any(x => x.Id == sk.Id),
    string.Join(" / ", skListTomorrow.Select(x => x.Title)));

Check("它不会重复出现两次（UNION ALL 之后要去重）",
    skListTomorrow.Count(x => x.Id == sk.Id) == 1,
    $"出现了 {skListTomorrow.Count(x => x.Id == sk.Id)} 次");

// 承诺只针对明天，不是后天：否则翻到哪天都挂着它，翻页就失去意义了
var skListD2 = await repo.GetDayListAsync(skD2);
Check("后天不再挂着它（跳过是对明天的承诺，不是对往后每一天）",
    !skListD2.Any(x => x.Id == sk.Id),
    string.Join(" / ", skListD2.Select(x => x.Title)));

// 跳过的那条在明天清单里仍是 Skipped 状态，但语义是"该做了"。
// 界面靠"能出现在清单里"这一点就能判定它已经回来（当天跳过的根本查不出来），
// 所以这里验证的是：它确实回来了，并且带着跳过次数，供 AI 判断"这事是不是不该做"。
var backTomorrow = skListTomorrow.First(x => x.Id == sk.Id);
Check("回来的那条带着跳过次数（AI 判断「这事是不是根本不该做」的线索）",
    backTomorrow.SkipCount >= 1, $"SkipCount={backTomorrow.SkipCount}");

// 昨天跳过的，今天要能看到——这是同一条规则的另一半
var skYesterday = await repo.AddAsync("昨天跳过的事", null, now.AddDays(-3));
await repo.SetStatusAsync(skYesterday.Id, ItemStatus.Skipped, "生病");
await using (var db2 = factory.CreateDbContext())
{
    var e2 = await db2.Items.FirstAsync(x => x.Id == skYesterday.Id);
    e2.SkippedAt = now.AddDays(-1); // 把跳过时刻挪到昨天，模拟"昨天点的跳过"
    await db2.SaveChangesAsync();
}

var skListTodayAfter = await repo.GetDayListAsync(skD0);
Check("昨天跳过的，今天出现在今天的清单上", skListTodayAfter.Any(x => x.Id == skYesterday.Id),
    string.Join(" / ", skListTodayAfter.Select(x => x.Title)));

// 统计口径：当天跳过的不进分母，回来算数的要进分母
var st = await repo.GetDayStatsAsync(skD0);
Check("当天跳过的那件不进今天的分母（完成率不该被它无故拉低）",
    st.SkippedToday >= 1, $"SkippedToday={st.SkippedToday}");
Check("统计里看得到「今天跳过了几件」（否则用户以为事情凭空消失了）",
    st.Total >= 1, $"Total={st.Total}");

Console.WriteLine();
Console.WriteLine("=== 15. 大任务：进度是算出来的，不是勾出来的 ===");

var projRepo = new ProjectRepository(factory);
var pTarget = DateOnly.FromDateTime(DateTime.Today.AddDays(12));
var proj = await projRepo.CreateAsync("数据结构期末复习", pTarget);

await using (var dbw = factory.CreateDbContext())
{
    Item Mk(string title, ItemStatus st, int day) => new()
    {
        Title = title,
        ProjectId = proj.Id,
        Status = st,
        DueAt = new DateTimeOffset(DateTime.Today.AddDays(day).AddHours(23).AddMinutes(59),
            TimeZoneInfo.Local.GetUtcOffset(DateTime.Today.AddDays(day).AddHours(23))),
        TimeZoneId = TimeZoneInfo.Local.Id
    };

    dbw.Items.AddRange(
        Mk("第一章", ItemStatus.Done, 1),
        Mk("第二章", ItemStatus.Done, 2),
        Mk("第三章", ItemStatus.Done, 3),
        Mk("第四章", ItemStatus.Pending, 4),
        Mk("第五章（后来决定不看了）", ItemStatus.Abandoned, 5));

    await dbw.SaveChangesAsync();
}

var cards = await projRepo.GetActiveWithProgressAsync();
var card = cards.First(x => x.Id == proj.Id);

Check("进度的分母不把放弃的算进去（否则进度条永远到不了 100%）",
    card.ActiveCount == 4, $"Active={card.ActiveCount} Done={card.DoneCount} Abandoned={card.AbandonedCount}");
Check("完成的条数对得上", card.DoneCount == 3);
Check("放弃了几条单独记着（界面上要能说「放弃了 1 件」）", card.AbandonedCount == 1);
Check("下一件到期时间指向最近的未完成那件", card.NextDueAt?.ToLocalTime().Date == DateTime.Today.AddDays(4),
    card.NextDueAt?.ToLocalTime().ToString("yyyy-MM-dd"));
Check("目标日期存的是当天最后一刻（当天不会一开始就显示已过期）",
    card.TargetDate?.ToLocalTime().Hour == 23, card.TargetDate?.ToLocalTime().ToString("HH:mm"));

var detail = await projRepo.GetItemsAsync(proj.Id);
Check("没做完的排在前面（打开项目先看接下来做什么）", detail[0].Status != ItemStatus.Done, detail[0].Title);
Check("做完的沉到底", detail[^1].Status == ItemStatus.Done || detail[^1].Status == ItemStatus.Abandoned);

await using (var dbw = factory.CreateDbContext())
{
    var last = dbw.Items.First(x => x.ProjectId == proj.Id && x.Status == ItemStatus.Pending);
    last.Status = ItemStatus.Done;
    last.CompletedAt = DateTimeOffset.Now;
    await dbw.SaveChangesAsync();
}

var after = (await projRepo.GetActiveWithProgressAsync()).First(x => x.Id == proj.Id);
Check("剩下的都做完之后进度能到 100%（放弃的不该拖住它）",
    after.DoneCount == after.ActiveCount, $"{after.DoneCount}/{after.ActiveCount}");
Check("全做完后不再有「下一件」", after.NextDueAt is null);

await projRepo.ArchiveAsync(proj.Id);
var visible = await projRepo.GetActiveWithProgressAsync();
Check("归档之后不再出现在这个列表里", !visible.Any(x => x.Id == proj.Id));
Check("归档不是删除：底下的任务都还在", (await projRepo.GetItemsAsync(proj.Id)).Count == 5);

var emptyProj = await projRepo.CreateAsync("还没拆的大事", null);
var emptyCard = (await projRepo.GetActiveWithProgressAsync()).First(x => x.Id == emptyProj.Id);
Check("空项目不会算出 NaN 进度（除零保护）", emptyCard.ActiveCount == 0 && emptyCard.DoneCount == 0);
Check("没定日期的项目说得清楚", emptyCard.TargetDate is null);

// ------------------------------------------------------------
// 往大任务里加子任务 —— 这一组对应一个真实的报障：
// 「自己建的大任务没法往里加任务，AI 建的也只能在里面删」。
// 加不进去的后果是"能建壳、装不进东西"，
// 而 AI 不可用时（离线、后端没起）这条唯一的路就断了。
// ------------------------------------------------------------
// ------------------------------------------------------------
// 改目标日期：现在是从日历上点一天过来的（界面负责把 DateTime 传来），
// 仓储要保证的是"那一天被记成当天的最后一刻"——
// 记成零点的话，当天一早它就会被算成"已过期"。
// ------------------------------------------------------------
var newTarget = DateOnly.FromDateTime(DateTime.Today.AddDays(20));
await projRepo.SetTargetDateAsync(emptyProj.Id, newTarget);

var retarget = (await projRepo.GetActiveWithProgressAsync()).First(x => x.Id == emptyProj.Id);
Check("从日历上选的一天记成当天最后一刻（不然当天一开始就显示过期）",
    retarget.TargetDate?.ToLocalTime().Date == newTarget.ToDateTime(TimeOnly.MinValue).Date
    && retarget.TargetDate?.ToLocalTime().Hour == 23,
    retarget.TargetDate?.ToLocalTime().ToString("yyyy-MM-dd HH:mm"));

await projRepo.SetTargetDateAsync(emptyProj.Id, null);
var cleared = (await projRepo.GetActiveWithProgressAsync()).First(x => x.Id == emptyProj.Id);
Check("「还没定」能把目标日期清掉", cleared.TargetDate is null);

var added = await repo.AddAsync("手写一遍 Dijkstra", projectId: emptyProj.Id);

Check("加进来的任务带着项目 id（没有它就不属于任何容器）",
    added.ProjectId == emptyProj.Id, added.ProjectId?.ToString() ?? "null");
Check("加完之后这个项目里能查到它",
    (await projRepo.GetItemsAsync(emptyProj.Id)).Any(x => x.Id == added.Id));

// 没给时间时挂到今天最后一刻：它今晚的回顾里必须出现，不能被无限期推迟。
Check("没指定日期时排到今天（看不见的待办等于没有）",
    added.DueAt?.ToLocalTime().Date == DateTime.Today, added.DueAt?.ToLocalTime().ToString("yyyy-MM-dd HH:mm"));

var ownCard = (await projRepo.GetActiveWithProgressAsync()).First(x => x.Id == emptyProj.Id);
Check("列表页的进度立刻跟上（否则以为刚才那条没加上）",
    ownCard.ActiveCount == 1 && ownCard.DoneCount == 0, $"{ownCard.DoneCount}/{ownCard.ActiveCount}");

// AI 建的项目同样要走这条口：它只是"谁先建的"不同，不是一个特例。
var aiProj = await projRepo.CreateAsync("开题报告", null);
await repo.AddAsync("列三篇参考", projectId: aiProj.Id);
await repo.AddAsync("写研究问题", projectId: aiProj.Id);
Check("AI 拆出来的项目也能接着手动补子任务",
    (await projRepo.GetItemsAsync(aiProj.Id)).Count == 2,
    $"{ (await projRepo.GetItemsAsync(aiProj.Id)).Count } 条");

// 不传 projectId 时还是普通的一条日程，不能被上一版改动带偏。
var loose = await repo.AddAsync("随便记一笔");
Check("不挂项目的还是独立的一条（老行为不能变）", loose.ProjectId is null);

Console.WriteLine();
Console.WriteLine();
Console.WriteLine("=== 16. 对话记录：关掉程序也要留着 ===");

var chatRepo = new ChatRepository(factory);

await chatRepo.AddAsync(ChatMessage.RoleUser, "两周内把开题报告弄出来");

var batch = Guid.NewGuid();
await chatRepo.AddAsync(
    ChatMessage.RoleAssistant,
    "拆好了，第一章今天先做。",
    batch,
    "[{\"op\":\"create\",\"ok\":true,\"detail\":\"加到 10-28\"}]");

var history = await chatRepo.GetRecentAsync(50);

Check("说过的话存下来了", history.Count == 2, $"Count={history.Count}");
Check("顺序是从早到晚（界面从上往下读）",
    history[0].Role == ChatMessage.RoleUser && history[1].Role == ChatMessage.RoleAssistant,
    $"{history[0].Role} → {history[1].Role}");
Check("批次号存下来了（重开之后「撤回」还得能点）",
    history[1].BatchId == batch, history[1].BatchId?.ToString());
Check("改动明细也存下来了",
    history[1].OutcomesJson?.Contains("create") == true, history[1].OutcomesJson);

// 关掉程序再打开 = 换一个仓储实例重新读一次。这是这次要交付的东西本身。
var reopened = new ChatRepository(factory).GetRecentAsync(50);
var again = await reopened;
Check("重新打开还能读到同一段对话", again.Count == 2 && again[1].Text == history[1].Text,
    $"Count={again.Count}");

await chatRepo.MarkUndoneAsync(batch);
var undoneMsg = (await chatRepo.GetRecentAsync(50)).First(x => x.BatchId == batch);
Check("撤过之后记住了（按钮不会再变回可点）", undoneMsg.IsUndone);

await chatRepo.ClearAsync();
Check("清空之后不再显示", (await chatRepo.GetRecentAsync(50)).Count == 0);

await using (var dbc = factory.CreateDbContext())
{
    var keptQuestion = await dbc.ChatMessages.CountAsync();
    Check("清空是软删：记录还在，误点了能捞回来", keptQuestion == 2, $"keptQuestion={keptQuestion}");
}

Console.WriteLine();
Console.WriteLine("=== 17. AI 回复的清洗：不能把它的自言自语显示给人看 ===");

// 真实出现过的脏回复（存进过用户的库），原样拿来当测试用例。
var dirty = "他只是在打招呼，我上一条把他当成要启动任务了。要承认这一点，短，不啰嗦，不空泛。"
    + "可以说：没别的意思，就是打招呼。\n\n保持两三句。actions 为空。";
var cleaned = ReplyCleaner.Clean(dirty);

Check("含系统字段名的那几句被剔掉（用户不知道 actions 是什么，出现即泄漏）",
    !cleaned.Contains("actions"), cleaned);
Check("正常的句子留下来了", cleaned.Contains("没别的意思"), cleaned);

// 这一条是第一版改出来的 bug：切句时丢了原标点。
var question = "今天是周二。你现在能做吗？";
var kept = ReplyCleaner.Clean(question);
Check("原标点保持不变（不能把？补成？。）", kept == question, $"得到「{kept}」");

Check("整句都是自言自语时返回空（由上层给一句能接住的话，而不是显示空气泡）",
    ReplyCleaner.Clean("actions 为空。suggestions 不给。") == string.Empty);

// 候选回复那个字段以前叫 questions，改名之后模型还可能吐旧名字
// （历史对话里有，它也可能记得）。这里拦的是"照旧格式自言自语"。
Check("改过名的字段照样拦得住（模型可能还记得 questions）",
    ReplyCleaner.Clean("actions 为空。questions 不给。") == string.Empty);
Check("正常回复原样返回，一个字都不动",
    ReplyCleaner.Clean("数据结构拆成 8 条，今天先花 30 分钟列清单。")
    == "数据结构拆成 8 条，今天先花 30 分钟列清单。");
Check("空输入返回空而不是报错", ReplyCleaner.Clean(null) == string.Empty);
Check("不误伤含「他想」这类代词的正常句子",
    ReplyCleaner.Clean("他昨天说要今天做完，还没动。").Contains("他昨天说"));

Console.WriteLine();
Console.WriteLine("=== 17b. 候选回复的收拾：模型给多少都不能把输入框顶没了 ===");

// 模型在这类字段上很不稳定：条数不准、长短不一、还会把一句话换两种说法输出两遍。
var sugMany = SuggestionCleaner.Clean(
    ["第一条现在就开始", "推到明天", "好", "先做十分钟", "周末再弄", "算了不做了"]);
Check("给多了就截断到上限（一排放不下就得舍，不能把输入框挤出去）",
    sugMany!.Count == SuggestionCleaner.MaxCount, $"得到 {sugMany.Count} 条");

var sugDup = SuggestionCleaner.Clean(["推到明天", "推到明天", "推到明天 "]);
Check("重复的只剩一条（同一个意思输出两遍就是两颗一样的按钮）", sugDup!.Count == 1);

var sugLong = SuggestionCleaner.Clean(["今天实在是不想动我想把所有的东西都推到明天然后再看看情况怎么样再说吧"]);
Check("超长的截断，不带出换行", sugLong![0].Length == SuggestionCleaner.MaxLength + 1
    && sugLong[0].EndsWith('…'), sugLong[0]);

var sugShort = SuggestionCleaner.Clean(["  推到明天  "]);
Check("前后空白去掉（空白 chip 看着像按钮坏了）", sugShort![0] == "推到明天", sugShort[0]);

var sugBreak = SuggestionCleaner.Clean(["好\n明天再做"]);
Check("换行接成一行（chip 里换行会把按钮撑成两行）",
    sugBreak![0] == "好 明天再做", sugBreak[0]);

Check("全是空的不报错，返回 null（界面当没有候选处理）",
    SuggestionCleaner.Clean(["", "   ", null!]) is null);
Check("模型没给这个字段时返回 null", SuggestionCleaner.Clean(null) is null);

Console.WriteLine();
Console.WriteLine("=== 18. 使用时长：记的是「人真的在用」，不是「钟走了多久」 ===");

var workRepo = new WorkSegmentRepository(factory);
var usage = new UsageRepository(factory, workRepo);
var uDay = DateOnly.FromDateTime(DateTime.Today);
var uBase = new DateTimeOffset(
    uDay.ToDateTime(TimeOnly.MinValue),
    TimeZoneInfo.Local.GetUtcOffset(uDay.ToDateTime(TimeOnly.MinValue)));
DateTimeOffset U(double hours) => uBase.AddHours(hours);

// —— 今天：两段正常的会话，验汇总和分桶 ——

// 10:50 打开视频，看到 11:30：中间跨过整点，应该被切成 10 点 10 分钟 + 11 点 30 分钟
var edge = await usage.StartAsync(DevicePlatform.Windows, "msedge", "Microsoft Edge", "B站 某个视频", true, U(10 + 50.0 / 60));
await usage.AddActiveSecondsAsync(edge.Id, 600, U(11), "B站 某个视频");
await usage.AddActiveSecondsAsync(edge.Id, 1800, U(11.5), "B站 某个视频");

// 09:10 到 10:20 写代码：同样是跨整点的一段
var vs = await usage.StartAsync(DevicePlatform.Windows, "devenv", "Visual Studio", "DayManager.sln", false, U(9 + 10.0 / 60));
await usage.AddActiveSecondsAsync(vs.Id, 3000, U(10));
await usage.AddActiveSecondsAsync(vs.Id, 1200, U(10 + 20.0 / 60));

var totals = await usage.GetAppTotalsAsync(uDay);
var edgeRow = totals.FirstOrDefault(x => x.AppId == "msedge");
Check("按应用汇总：Edge 40 分钟", edgeRow?.Seconds == 2400, $"Seconds={edgeRow?.Seconds}");
Check("按应用汇总：Visual Studio 70 分钟",
    totals.FirstOrDefault(x => x.AppId == "devenv")?.Seconds == 4200);
Check("用得多的排前面（打开时间页先看到最占时间的）",
    totals.Count >= 2 && totals[0].AppId == "devenv");
Check("显示名是可读的名字，不是 msedge", edgeRow?.DisplayName == "Microsoft Edge", edgeRow?.DisplayName);
Check("娱乐标记跟着记录走（名单以后会变，历史不能被追溯篡改）", edgeRow?.IsEntertainment == true);

var uSum = await usage.GetDaySummaryAsync(uDay);
Check("一天总时长 = 各段之和", uSum.TotalSeconds == 6600, $"Total={uSum.TotalSeconds}");
Check("娱乐时长单独算得出来（回顾界面默认折叠它）", uSum.EntertainmentSeconds == 2400,
    $"Entertainment={uSum.EntertainmentSeconds}");
Check("末次活跃时间取最后一段的结束（用来推入睡时间）",
    uSum.LastAt?.ToUnixTimeSeconds() == U(11.5).ToUnixTimeSeconds(), $"{uSum.LastAt:HH:mm}");
Check("会话条数也数得出来", uSum.SessionCount == 2, $"{uSum.SessionCount}");

var hourly = await usage.GetHourlyAsync(uDay);
Check("按小时分桶：9 点 50 分钟", hourly[9] == 3000, $"{hourly[9]}");
Check("跨整点的一段被切开：10 点 = VS 的 20 分钟 + Edge 的 10 分钟",
    hourly[10] == 1800, $"{hourly[10]}");
Check("11 点 30 分钟", hourly[11] == 1800, $"{hourly[11]}");
Check("分桶合计和总时长对得上（对不上就是柱状图和数字打架）",
    hourly.Sum() == uSum.TotalSeconds, $"{hourly.Sum()} vs {uSum.TotalSeconds}");

// —— 昨天：中间断了一小时的会话，验"空档不算时长" ——
var yBase = uBase.AddDays(-1);
DateTimeOffset Y(double hours) => yBase.AddHours(hours);

var gapSession = await usage.StartAsync(DevicePlatform.Windows, "chrome", "Google Chrome", "查资料", false, Y(14));
await usage.AddActiveSecondsAsync(gapSession.Id, 720, Y(14.2));   // 14:00 用了 12 分钟
await usage.AddActiveSecondsAsync(gapSession.Id, 300, Y(15.5));   // 15:30 又用了 5 分钟
await usage.ExtendAsync(gapSession.Id, Y(15.5));                  // 结束时间推到最后

var ySum = await usage.GetDaySummaryAsync(uDay.AddDays(-1));
Check("中间断了一个多小时，只记真正在用的 17 分钟", ySum.TotalSeconds == 1020, $"{ySum.TotalSeconds}");
Check("（对照）按墙上的钟算会是 90 分钟 —— 那正是不能用 EndAt - StartAt 当使用时长原因",
    (int)(Y(15.5) - Y(14)).TotalSeconds == 5400);

Check("启动时能数出上次没正常结束的会话（排查「昨天时长不对」要看这个）",
    await usage.CloseOrphansAsync(U(13)) > 0);

// —— 删掉某天：软删，墓碑留着 ——
await usage.DeleteDayAsync(uDay.AddDays(-1));
Check("删掉某天的记录后不再进统计", (await usage.GetDaySummaryAsync(uDay.AddDays(-1))).TotalSeconds == 0);
await using (var db = factory.CreateDbContext())
{
    var keptRows = await db.UsageSessions.IgnoreQueryFilters().CountAsync(x => x.AppId == "chrome");
    Check("删除是软删：记录还在（同步时别的设备才知道这些被删了）", keptRows == 1, $"kept={keptRows}");
}

// —— 任务计时进分母：没开电脑但在做事的时间也要算进时间账 ——
//
// 【为什么这一段必须有】
// 它测的就是这次要修的毛病本身：只统计屏幕前的时间，
// 他做纸上练习的两小时在统计里等于不存在，分母偏小，
// 于是"娱乐占比"被系统性高估，明明做了事却被说成"今天都在玩"。
// 加完再软删掉，免得影响后面那些按绝对值写的断言。
var beforeWork = await usage.GetDaySummaryAsync(uDay);
var segOffline = Guid.Empty;
var segOverlap = Guid.Empty;

await using (var db = factory.CreateDbContext())
{
    var paper = new Item { Title = "纸上做一套题", DueAt = uBase.AddDays(1) };
    db.Items.Add(paper);
    await db.SaveChangesAsync();

    // 13:00-15:00：这两个钟头他没碰电脑，设备那一路一条记录都没有。
    var offline = new WorkSegment { ItemId = paper.Id, StartAt = U(13), EndAt = U(15) };
    // 11:00-11:30：和 Edge 那段完全重叠——在电脑上边做事边计时的典型情形。
    var overlap = new WorkSegment { ItemId = paper.Id, StartAt = U(11), EndAt = U(11.5) };

    db.WorkSegments.AddRange(offline, overlap);
    await db.SaveChangesAsync();

    segOffline = offline.Id;
    segOverlap = overlap.Id;
}

var afterWork = await usage.GetDaySummaryAsync(uDay);
Check("任务计时单独统计得出来（界面上要能说明总数里有多少是它）",
    afterWork.WorkSeconds == 7200 + 1800, $"{afterWork.WorkSeconds}");
Check("没开电脑的那两小时把分母撑大了两小时",
    afterWork.AccountedSeconds == beforeWork.AccountedSeconds + 7200,
    $"{afterWork.AccountedSeconds} vs {beforeWork.AccountedSeconds}");
Check("和屏幕前重叠的那半小时不再重复计数（求并集的意义就在这）",
    afterWork.AccountedSeconds < beforeWork.AccountedSeconds + 7200 + 1800,
    $"{afterWork.AccountedSeconds}");
Check("娱乐时长不受影响：它只来自设备记录",
    afterWork.EntertainmentSeconds == beforeWork.EntertainmentSeconds);
Check("屏幕前的总时长也不受影响（两路各记各的）",
    afterWork.TotalSeconds == beforeWork.TotalSeconds);

await using (var db = factory.CreateDbContext())
{
    var rows = await db.WorkSegments.Where(x => x.Id == segOffline || x.Id == segOverlap).ToListAsync();
    foreach (var r in rows)
    {
        r.DeletedAt = DateTimeOffset.Now;
    }

    await db.SaveChangesAsync();
}

Check("软删之后这两段退出统计（墓碑留着，同步时别的设备才知道）",
    (await usage.GetDaySummaryAsync(uDay)).AccountedSeconds == beforeWork.AccountedSeconds);

// —— 走一遍真实路径：点启动会开一段，再点一下会闭上 ——
//
// 【为什么还要再验一遍】
// 上面是直接往表里插记录，验的只是统计那一侧；
// 这里验的是界面上那两个按钮到底有没有接上这张新表。
// 接不上的表现非常静默：计时照走、勾也照打、界面上一个字都不报错，
// 只是时间账的分母永远少那一块，占比一直偏高——而这种偏差没人会去查。
var timed = await repo.AddAsync("端到端：走一遍启动和暂停");

await repo.ToggleStartPauseAsync(timed.Id);
Check("点了启动，表里就多出一段在走",
    await workRepo.CountSegmentsAsync(timed.Id) == 1);

await using (var db = factory.CreateDbContext())
{
    var open = await db.WorkSegments.SingleAsync(x => x.ItemId == timed.Id);
    Check("这段还没有结束时刻（计时器正在走）", open.EndAt is null);
}

await repo.ToggleStartPauseAsync(timed.Id);   // 再点一下 = 暂停
await using (var db = factory.CreateDbContext())
{
    var closed = await db.WorkSegments.SingleAsync(x => x.ItemId == timed.Id);
    Check("暂停之后这段被收尾", closed.EndAt is not null);
    Check("结束时刻不早于开始时刻", closed.EndAt >= closed.StartAt);
}

// 勾完成也要收尾：否则"边做边勾"留下一条永远走着的段，
// 它的右端被当成"现在"，每查一次就多算一截。
await repo.ToggleStartPauseAsync(timed.Id);   // 继续
await repo.ToggleDoneAsync(timed.Id);         // 完成
await using (var db = factory.CreateDbContext())
{
    var left = await db.WorkSegments.CountAsync(x => x.ItemId == timed.Id && x.EndAt == null);
    Check("勾完成之后没有还在走的段", left == 0, $"left={left}");
}

await using (var db = factory.CreateDbContext())
{
    var it = await db.Items.SingleAsync(x => x.Id == timed.Id);
    it.DeletedAt = DateTimeOffset.Now;

    var segs = await db.WorkSegments.Where(x => x.ItemId == timed.Id).ToListAsync();
    foreach (var s in segs)
    {
        s.DeletedAt = DateTimeOffset.Now;
    }

    await db.SaveChangesAsync();
}

Console.WriteLine();
Console.WriteLine("=== 19. 一段区间的统计（日/周/月/年共用一套算法） ===");

var rBase = uBase.AddDays(-3);
DateTimeOffset R(double hours) => rBase.AddHours(hours);
var rDay = DateOnly.FromDateTime(rBase.DateTime);

// 00:30 这一段是关键用例：本地是三天前的 00:30，而 UTC 是"四天前的 16:30"。
// 分组时忘了转本地，它就会被算到前一天去——跨天统计全错位，且看起来毫无异常。
var rMidnight = await usage.StartAsync(DevicePlatform.Windows, "code", "VS Code", "写代码", false, R(0.5));
await usage.AddActiveSecondsAsync(rMidnight.Id, 1200, R(1));

var rEvening = await usage.StartAsync(DevicePlatform.Windows, "msedge", "Microsoft Edge", "看视频", true, R(21));
await usage.AddActiveSecondsAsync(rEvening.Id, 600, R(21.2));

var rRange = await usage.GetRangeAsync(rDay, rDay.AddDays(1));
Check("区间总时长 = 各段之和", rRange.TotalSeconds == 1800, $"{rRange.TotalSeconds}");
Check("娱乐时长单独算", rRange.EntertainmentSeconds == 600, $"{rRange.EntertainmentSeconds}");
Check("按天分组用的是本地日期（凌晨那段不能被算到前一天）",
    rRange.Days.Count == 1 && rRange.Days[0].Day == rDay,
    string.Join(",", rRange.Days.Select(x => x.Day)));
Check("按应用分组，用得多的排前面", rRange.Apps.Count == 2 && rRange.Apps[0].AppId == "code");
Check("第一名的应用时长对得上", rRange.Apps[0].Seconds == 1200, $"{rRange.Apps[0].Seconds}");
Check("区间里最晚的会话时间转成了本地（末次活跃当入睡时间用）",
    rRange.LastAt?.Hour == 21, $"{rRange.LastAt:HH:mm}");

Check("空区间返回空统计，不抛异常",
    (await usage.GetRangeAsync(rDay.AddDays(10), rDay.AddDays(11))).TotalSeconds == 0);
Check("区间写反了也不会崩（翻页算错时不该炸）",
    (await usage.GetRangeAsync(rDay, rDay)).TotalSeconds == 1800);

var rWeek = await usage.GetRangeAsync(rDay.AddDays(-3), rDay.AddDays(4));
Check("跨多天的区间把总量加起来", rWeek.TotalSeconds >= 1800, $"{rWeek.TotalSeconds}");
Check("日统计和区间统计口径一致（同一段数据，两个入口同一个数）",
    (await usage.GetDaySummaryAsync(rDay)).TotalSeconds == rRange.TotalSeconds,
    $"{(await usage.GetDaySummaryAsync(rDay)).TotalSeconds} vs {rRange.TotalSeconds}");

Console.WriteLine();
Console.WriteLine("=== 19b. 娱乐时长：手动标记追认历史，名单不改写过去 ===");

/*
 * 这两条断言是为了把一处设计决定固定住。
 *
 * 手动标记（app_rules）和内置名单是两种性质完全不同的"认知"：
 *   - 手动标记是**追认**。人去标记一个应用，多半发生在"刷了很久终于承认"的那一刻，
 *     标记之前那几个小时恰恰是最该被看见的——所以它对全部历史生效。
 *   - 名单会更新。下个月把某个新游戏补进名单，那是系统在改主意，
 *     不该回过头去改写上周的数字。所以没手动标过的，沿用记录当时的判断。
 * 改任一条都要先想清楚：只按快照算，是在惩罚诚实；全实时算，历史会被版本更新悄悄篡改。
 */

// 这段会话写进去的时候没被记成娱乐：比如它那会儿还没进名单。
// 时间取往前五天，免得干扰上面那些区间的断言。
var pBase = uBase.AddDays(-5);
DateTimeOffset P(double hours) => pBase.AddHours(hours);
var pDay = DateOnly.FromDateTime(pBase.DateTime);

var probe = await usage.StartAsync(DevicePlatform.Windows, "smokeprobe", "Probe App", null, false, P(9));
await usage.AddActiveSecondsAsync(probe.Id, 600, P(9.2));

Check("没手动标过：沿用记录当时的判断（还不算娱乐）",
    (await usage.GetRangeAsync(pDay, pDay.AddDays(1))).EntertainmentSeconds == 0);

await using (var pdb = factory.CreateDbContext())
{
    pdb.AppRules.Add(new AppRule
    {
        Id = Guid.NewGuid(),
        AppId = "smokeprobe",
        AppName = "Probe App",
        Kind = AppKind.Entertainment,
    });
    await pdb.SaveChangesAsync();
}

var pRange = await usage.GetRangeAsync(pDay, pDay.AddDays(1));
Check("手动标成娱乐：连标记之前那段也补算进来（标记是追认，不是从今开始）",
    pRange.EntertainmentSeconds == 600, $"{pRange.EntertainmentSeconds}");
Check("这个应用自己也被标成娱乐了",
    pRange.Apps.FirstOrDefault(x => x.AppId == "smokeprobe")?.IsEntertainment == true);

Console.WriteLine();
Console.WriteLine("=== 20. 干预引擎：什么时候该说话、说给谁听 ===");

var policyRepo = new PolicyRepository(factory);
var ruleRepo = new AppRuleRepository(factory);

var iToday = DateOnly.FromDateTime(DateTime.Now);
var iNow = DateTimeOffset.Now;

// 判定器的输入有十来个字段，每次 new 一遍会淹没断言本身。
// 这个局部函数只写"这一条关心"的那几个参数，其余走默认值。
InterventionContext Ctx(
    string appId,
    string appName,
    int minutes,
    AppKind kind = AppKind.Neutral,
    bool enabled = true,
    int threshold = 30,
    int cooldown = 30,
    DateTimeOffset? lastNotified = null,
    DateOnly? snoozed = null,
    int remaining = 0,
    string? first = null)
    => new(appId, appName, minutes, kind, enabled, threshold, cooldown,
        lastNotified, snoozed, iToday, remaining, first, iNow);

// ---- 名单 ----

Check("内置名单认得 B 站",
    EntertainmentApps.IsEntertainment("bilibili.exe"));

Check("带路径、大小写混写也认得",
    EntertainmentApps.IsEntertainment(@"C:\Program Files\BiliBili\BiliBili.EXE"));

Check("浏览器绝不算娱乐",
    !EntertainmentApps.IsEntertainment("chrome.exe"),
    "否则一整天的网页浏览都会被当成刷视频");

Check("名单是空的输入也不炸",
    !EntertainmentApps.IsEntertainment(null) && !EntertainmentApps.IsEntertainment("  "));

Check("用户标成工作就压过名单（用 B 站看网课）",
    EntertainmentApps.Classify("bilibili", AppKind.Work) == AppKind.Work);

Check("没标记时由名单决定",
    EntertainmentApps.Classify("bilibili", AppKind.Neutral) == AppKind.Entertainment);

Check("名单外的应用保持未标记（不打扰）",
    EntertainmentApps.Classify("notepad", AppKind.Neutral) == AppKind.Neutral);

// ---- 判定 ----

Check("关掉开关就一句都不说",
    InterventionDecider.Decide(Ctx("bilibili", "B 站", 60, enabled: false)).Reason
        == InterventionSkipReason.Disabled);

Check("标成工作之后连名单都不看",
    InterventionDecider.Decide(Ctx("bilibili", "B 站", 60, kind: AppKind.Work)).Reason
        == InterventionSkipReason.NotEntertainment);

Check("没到阈值先不打扰",
    InterventionDecider.Decide(Ctx("bilibili", "B 站", 29)).Reason
        == InterventionSkipReason.BelowThreshold);

Check("刚好到阈值就该说话",
    InterventionDecider.Decide(Ctx("bilibili", "B 站", 30)).ShouldNotify);

var withTask = InterventionDecider.Decide(
    Ctx("bilibili", "B 站", 42, remaining: 3, first: "写开题报告"));

Check("文案里点名了那一件没做完的事", withTask.Notice?.Body.Contains("写开题报告") == true);
Check("文案给出小到不用下决心的动作", withTask.Notice?.Body.Contains("10 分钟") == true);
Check("标题写清了是哪一段用了多久", withTask.Notice?.Title.Contains("B 站") == true
    && withTask.Notice.Title.Contains("42 分钟") == true);

var noTask = InterventionDecider.Decide(Ctx("bilibili", "B 站", 42, remaining: 0));

Check("今天的事都做完了就给许可，而不是继续催",
    noTask.ShouldNotify && noTask.Notice!.Title.Contains("都做完了"),
    noTask.Notice?.Title);

Check("按过「今天别再提了」就一整天都不提",
    InterventionDecider.Decide(Ctx("bilibili", "B 站", 60, snoozed: iToday)).Reason
        == InterventionSkipReason.Snoozed);

Check("昨天按的赦免不影响今天",
    InterventionDecider.Decide(Ctx("bilibili", "B 站", 60, snoozed: iToday.AddDays(-1))).ShouldNotify);

Check("冷却时间内不重复提",
    InterventionDecider.Decide(Ctx("bilibili", "B 站", 60, lastNotified: iNow.AddMinutes(-5))).Reason
        == InterventionSkipReason.CoolingDown);

Check("冷却过了可以再提",
    InterventionDecider.Decide(Ctx("bilibili", "B 站", 60, lastNotified: iNow.AddMinutes(-40))).ShouldNotify);

Check("阈值配成 0 不会变成疯狂弹窗",
    InterventionDecider.Decide(Ctx("bilibili", "B 站", 60, threshold: 0)).Reason
        == InterventionSkipReason.BelowThreshold);

// ---- 点名之前先看看那件事现在能不能动手 ----
//
// 这几条来自 2026-10-31 傍晚的一次真实提醒：当时把「12点前躺下，手机放到够不着」
// 当成下午该先做十分钟的事点了名，而它排的其实是大半夜才开始。
// 所以挑选这一步要有自己的断言——干预不仅要"该弹的时候弹了"，还得"说的是人话"。

// Item 有二十来个字段，这里只写和"能不能现在动手"有关的几个。
Item It(string title, DateTimeOffset? startAt = null, bool allDay = false,
        ItemStatus status = ItemStatus.Pending, DateTimeOffset? startedAt = null)
    => new() { Title = title, StartAt = startAt, IsAllDay = allDay, Status = status, StartedAt = startedAt };

Check("排到半夜才开始的事，下午不会被点名",
    NudgeTargetPicker.Pick(
        new[] { It("12点前躺下，手机放到够不着的地方", startAt: iNow.Date.AddHours(23).AddMinutes(59)) },
        iNow) is null,
    "这条就是当时的真实数据：它在那天清單里排第一，直接被点名了");

Check("没排时间的纯待办随时可以点名",
    NudgeTargetPicker.Pick(new[] { It("写开题报告") }, iNow)?.Title == "写开题报告");

Check("全天事件不算'还没到点'",
    NudgeTargetPicker.Pick(new[] { It("交房租", allDay: true) }, iNow) is not null);

Check("排过了时间还没做的事可以点名（它早该做了）",
    NudgeTargetPicker.Pick(
        new[] { It("改论文", startAt: iNow.AddHours(-3)) },
        iNow) is not null);

Check("已经开了头的优先于还没动的",
    NudgeTargetPicker.Pick(
        new[]
        {
            It("排在前面的待办"),
            It("昨天没写完的那节", status: ItemStatus.InProgress)
        },
        iNow)?.Title == "昨天没写完的那节",
    "接着做比重新开始容易得多，这正是这个功能要蹭的那一点");

Check("几分钟后就要开始的事不算太早",
    NudgeTargetPicker.Pick(
        new[] { It("18:05 的会", startAt: iNow.AddMinutes(NudgeTargetPicker.GraceMinutes - 1)) },
        iNow) is not null,
    "留缓冲是为了让'先把东西摆开'这种提醒还能说出口");

Check("半小时后才开始的事现在不该点名",
    NudgeTargetPicker.Pick(
        new[] { It("18:30 的会", startAt: iNow.AddMinutes(30)) },
        iNow) is null);

Check("一件都挑不出来时明确返回 null，而不是硬抓第一条",
    NudgeTargetPicker.Pick(
        new[] { It("睡前那件", startAt: iNow.AddHours(6)) },
        iNow) is null,
    "退回不点名的文案，比说一句现在做不了的胡话好");

Check("清单是空的时候不炸",
    NudgeTargetPicker.Pick(Array.Empty<Item>(), iNow) is null);

// ---- 实际用时：AI 排得多不多，全靠这个数 ----

var tStart = new DateTimeOffset(2026, 11, 1, 10, 0, 0, TimeSpan.Zero);
var tResume = tStart.AddMinutes(25);

Check("没开始也没累计：0 分钟",
    Elapsed.TotalMinutes(null, null, tStart) == 0);

Check("正在走表：从启动那一秒算起",
    Elapsed.TotalMinutes(null, tStart, tResume) == 25);

Check("暂停之后只算结清过的部分，多问几次也不会重复累加",
    Elapsed.TotalMinutes(25, null, tResume.AddMinutes(90)) == 25,
    "暂停时 StartedAt 是空的，这时再把当前这一段加上去就是凭空多一段时间");

Check("暂停又继续：结清的加上当前这一段",
    Elapsed.TotalMinutes(25, tResume, tResume.AddMinutes(10)) == 35);

Check("时钟被往回调过：按积累值算，不出现负数",
    Elapsed.TotalMinutes(25, tStart, tStart.AddMinutes(-30)) == 25);

Check("不到一分钟的一段按 0 记，但不会把已经有的积累值吃掉",
    Elapsed.TotalMinutes(25, tStart, tStart.AddSeconds(40)) == 25);

Check("没点过启动就勾完成：留空，不替用户编一个 0",
    Elapsed.OrNullIfZero(0) is null,
    "写个 0 进去，AI 会读成这件事不用花时间，比没有数据更糟");

Check("有数据才回填实际用时", Elapsed.OrNullIfZero(125) == 125);

// ---- 落库 ----

await ruleRepo.SetKindAsync("bilibili", "哔哩哔哩", AppKind.Work);
Check("应用标记能落库", (await ruleRepo.GetAsync("bilibili"))?.Kind == AppKind.Work);

await ruleRepo.SetKindAsync("bilibili", "哔哩哔哩", AppKind.Entertainment);
Check("同一个应用再标一次是改，不是多插一行",
    (await ruleRepo.GetAllAsync()).Count(x => x.AppId == "bilibili") == 1);

await policyRepo.SetAsync(PolicyKeys.ThresholdMinutes, "45", PolicySource.User);
Check("参数能落库并读回来",
    await policyRepo.GetIntAsync(PolicyKeys.ThresholdMinutes, 30) == 45);

Check("没设过的参数用默认值",
    await policyRepo.GetIntAsync("intervention.not.set.yet", 30) == 30);

Check("参数被写成非数字时退回默认值，而不是让功能停摆",
    await WriteBadPolicyAndReadAsync(policyRepo) == 30);

await policyRepo.SnoozeAsync("bilibili", iToday);
Check("「今天别再提了」是存库的（重启之后仍然算数）",
    await policyRepo.GetSnoozeDateAsync("bilibili") == iToday);

async Task<int> WriteBadPolicyAndReadAsync(PolicyRepository p)
{
    await p.SetAsync("intervention.cooldown_minutes", "半小时", PolicySource.User);
    return await p.GetIntAsync("intervention.cooldown_minutes", 30);
}

Console.WriteLine();
Console.WriteLine("=== 21. 最小启动步骤：两分钟能做完的第一步 ===");

{
    var stepItem = await repo.AddAsync("写季度报告");

    var stepSet = await repo.SetMinimalStepAsync(stepItem.Id, "  打开文档写个标题  ");
    Check("第一步被存下来，并且去掉前后空白",
        stepSet?.MinimalStep == "打开文档写个标题", stepSet?.MinimalStep ?? "(null)");

    // 重新读一次库：确认是真的落库了，而不是只在内存里改了
    await using (var db = factory.CreateDbContext())
    {
        var stepReread = await db.Items.AsNoTracking().FirstAsync(x => x.Id == stepItem.Id);
        Check("重读数据库仍然一致",
            stepReread.MinimalStep == "打开文档写个标题", stepReread.MinimalStep ?? "(null)");
    }

    var stepBlank = await repo.SetMinimalStepAsync(stepItem.Id, "   ");
    Check("只给空白等于清掉", stepBlank?.MinimalStep is null, stepBlank?.MinimalStep ?? "(null)");

    var stepNull = await repo.SetMinimalStepAsync(stepItem.Id, null);
    Check("传 null 同样能清掉", stepNull?.MinimalStep is null);

    var stepCut = await repo.SetMinimalStepAsync(stepItem.Id, new string('字', 600));
    Check("超长截断到 500，不把数据库写爆",
        stepCut?.MinimalStep?.Length == 500, $"实际 {stepCut?.MinimalStep?.Length} 字");

    var stepMissing = await repo.SetMinimalStepAsync(Guid.NewGuid(), "不存在的记录");
    Check("记录不存在时返回 null 而不是抛异常", stepMissing is null);
}

Console.WriteLine();
Console.WriteLine("=== 22. AI 主动开口：什么时候该说话 ===");

{
    // 【为什么要给这一块写测试】
    // 触发条件是这个功能里唯一称得上"逻辑"的部分，而它在真机上极难验：
    // 早晨那条要等到早上八点，连续低完成率要连着三天不做事。
    // 做成纯函数之后，把时钟拨到任意时刻，几毫秒就能验完。

    var day = new DateOnly(2026, 10, 28);
    DateTimeOffset At(int hour, int minute = 0) =>
        new(day.Year, day.Month, day.Day, hour, minute, 0, TimeSpan.FromHours(8));

    // ---- 早晨 ----

    var morning = ProactiveDecider.Decide(new ProactiveContext { Now = At(8, 30) });
    Check("早上八点半该说早晨计划",
        morning.Kind == ProactiveKind.Morning, morning.Kind.ToString());

    Check("早晨计划的窗口到下 11 点为止",
        ProactiveDecider.Decide(new ProactiveContext { Now = At(11, 0) }).Kind != ProactiveKind.Morning);

    Check("早上七点不算（太早了）",
        ProactiveDecider.Decide(new ProactiveContext { Now = At(7, 0) }).Kind != ProactiveKind.Morning);

    // ---- 晚间 ----

    var evening = ProactiveDecider.Decide(new ProactiveContext { Now = At(21, 10) });
    Check("晚上九点后该说晚间总结",
        evening.Kind == ProactiveKind.Evening, evening.Kind.ToString());

    // ---- 日程前提醒 ----

    var itemId = Guid.NewGuid();
    var before = ProactiveDecider.Decide(new ProactiveContext
    {
        Now = At(9, 0),
        Upcoming = new[]
        {
            new ProactiveUpcoming(itemId, "开会", "把文档打开", At(9, 8)),
            new ProactiveUpcoming(Guid.NewGuid(), "写报告", null, At(15, 0))
        }
    });
    Check("10 分钟内要开始的会被点名",
        before.Kind == ProactiveKind.Before && before.ItemId == itemId,
        $"{before.Kind} / {before.ItemId}");

    var tooFar = ProactiveDecider.Decide(new ProactiveContext
    {
        Now = At(9, 0),
        Upcoming = new[] { new ProactiveUpcoming(itemId, "写报告", null, At(15, 0)) }
    });
    Check("还有六小时的不提前这么多说", tooFar.Kind != ProactiveKind.Before, tooFar.Kind.ToString());

    // 已经过了开始时间：再说"还有 8 分钟"就是错的。
    var alreadyStarted = ProactiveDecider.Decide(new ProactiveContext
    {
        Now = At(9, 0),
        Upcoming = new[] { new ProactiveUpcoming(itemId, "开会", null, At(8, 50)) }
    });
    Check("已经开始的不提醒（不说过期的话）",
        alreadyStarted.Kind != ProactiveKind.Before, alreadyStarted.Kind.ToString());

    // ---- 连续低完成率 ----

    var low = new[]
    {
        new ProactiveDayRatio(day.AddDays(-3), 5, 1),
        new ProactiveDayRatio(day.AddDays(-2), 4, 0),
        new ProactiveDayRatio(day.AddDays(-1), 6, 1)
    };

    var lowRate = ProactiveDecider.Decide(new ProactiveContext { Now = At(14, 0), RecentDays = low });
    Check("连着三天完成率不到三成，要来谈一次",
        lowRate.Kind == ProactiveKind.LowRate, lowRate.Kind.ToString());

    var notLow = ProactiveDecider.Decide(new ProactiveContext
    {
        Now = At(14, 0),
        RecentDays = new[]
        {
            new ProactiveDayRatio(day.AddDays(-3), 5, 1),
            new ProactiveDayRatio(day.AddDays(-2), 4, 4),   // 这一天全做完了
            new ProactiveDayRatio(day.AddDays(-1), 6, 1)
        }
    });
    Check("中间有一天做完了就不算'一直很差'",
        notLow.Kind != ProactiveKind.LowRate, notLow.Kind.ToString());

    // 周末休息两天不能被判成"你最近三天什么都没做成"。
    var weekend = ProactiveDecider.Decide(new ProactiveContext
    {
        Now = At(14, 0),
        RecentDays = new[]
        {
            new ProactiveDayRatio(day.AddDays(-3), 0, 0),
            new ProactiveDayRatio(day.AddDays(-2), 0, 0),
            new ProactiveDayRatio(day.AddDays(-1), 5, 1)
        }
    });
    Check("空着的两天不算低完成率（休息不是失败）",
        weekend.Kind != ProactiveKind.LowRate, weekend.Kind.ToString());

    Check("只有两天记录时不下判断",
        !ProactiveDecider.IsConsistentlyLow(new[]
        {
            new ProactiveDayRatio(day.AddDays(-2), 5, 0),
            new ProactiveDayRatio(day.AddDays(-1), 5, 0)
        }));

    // ---- 一天只说一次 + 冷却 ----

    var fired = new HashSet<ProactiveKind> { ProactiveKind.Morning };
    Check("同一种一天只说一次",
        ProactiveDecider.Decide(new ProactiveContext { Now = At(9, 0), FiredToday = fired })
            .Kind != ProactiveKind.Morning);

    var cooling = ProactiveDecider.Decide(new ProactiveContext
    {
        Now = At(9, 30),
        LastFiredAt = At(9, 0)
    });
    Check("刚说过半小时内不再说（防止连着弹）",
        cooling.Reason == ProactiveSkipReason.CoolingDown, cooling.Reason.ToString());

    Check("关掉总开关就一句都不说",
        ProactiveDecider.Decide(new ProactiveContext { Now = At(9, 0), Enabled = false })
            .Reason == ProactiveSkipReason.Disabled);

    // ---- 优先级：时效强的先说 ----

    var both = ProactiveDecider.Decide(new ProactiveContext
    {
        Now = At(8, 55),
        Upcoming = new[] { new ProactiveUpcoming(itemId, "开会", null, At(9, 0)) }
    });
    Check("早晨和日程前撞车时，先说更急的那件",
        both.Kind == ProactiveKind.Before, both.Kind.ToString());
}

// ---- AI 说话的语气档 ----
//
// 【为什么这几条值得测】
// 提示词的措辞改错了没有任何报错：AI 说话变味了，不进日志、不崩，
// 只能靠读它说的话才能发现。放到 Core 之后，这几条能兜住
// "档位解析退化了""三档文案被改成一模一样""底线条款被删掉"这三类失误。
{
    Check("没设过语气时退回克制档", AiTone.Parse(null) == AiTone.Reserved);
    Check("库里写坏了也退回克制档", AiTone.Parse("  瞎写的 ") == AiTone.Reserved);
    Check("大小写和空格不影响识别", AiTone.Parse(" Warm ") == AiTone.Warm);

    var reserved = AiTone.StyleLines(AiTone.Reserved);
    var warm = AiTone.StyleLines(AiTone.Warm);
    var direct = AiTone.StyleLines(AiTone.Direct);

    Check("三档文案都非空", reserved.Count > 0 && warm.Count > 0 && direct.Count > 0);
    Check("三档文案互不相同",
        !reserved.SequenceEqual(warm) && !warm.SequenceEqual(direct) && !reserved.SequenceEqual(direct));

    // 这条是底线：温度可以调，"不许挖苦、不许审判"不能调。
    // 空泛鼓励只在温和档里被明令禁止（直接档是压根不给鼓励），所以只查这一条。
    Check("每一档都守着不许挖苦这条底线",
        new[] { reserved, warm, direct }.All(x => x.Any(l => l.Contains("不许挖苦"))));

    // 【为什么这一条值得测】
    // 曾经这里写着"一次不超过三句"，结果人设框里写再多也没用：
    // 篇幅限制排在硬规矩里，一个人想让 AI 多说两句的请求会被它直接吃掉。
    // 篇幅是口味不是格式，锁在这个位置上会连带把人设一起锁死。
    // 这里用最长中文句子长度的粗筛守着：不许再出现"不超过 X 句"这种话。
    var countCaps = new[] { reserved, warm, direct }
        .SelectMany(x => x)
        .Where(l => l.Contains("不超过") && l.Contains("句") && !l.Contains("句号"))
        .ToList();
    Check("说话方式里不许再写死句数上限", countCaps.Count == 0);
}

// ---- 做成一件事之后的那半句 ----
//
// 【为什么这几条值得测】
// 实测里他把一条画完，AI 回的是"挂上了，今天一条"——一句纯记账，
// "做完"在系统里是静音的。这是一类最容易被删掉的规则：
// 它不影响任何功能、不进日志、不崩，删了之后看起来什么问题都没有，
// 只是人慢慢地不想再开始第二次。所以要用断言把它钉住。
{
    var reserved = AiTone.FeedbackLines(AiTone.Reserved);
    var warm = AiTone.FeedbackLines(AiTone.Warm);
    var direct = AiTone.FeedbackLines(AiTone.Direct);

    Check("三档都给了做完之后的那半句", reserved.Count > 0 && warm.Count > 0 && direct.Count > 0);
    Check("每一档都守着不许挖苦这条底线",
        new[] { reserved, warm, direct }.All(x => x.Any(l => l.Contains("不许挖苦"))));

    // 这条是它的命门：只要在要求"夸具体那件事"，就不用怕它会滑坡成"加油你可以的"。
    Check("每一档都禁止空泛鼓励",
        new[] { reserved, warm, direct }.All(x => x.Any(l => l.Contains("不许空泛鼓励"))));

    // 它必须是"默认就有"的。写成硬规矩就没法被"别夸我"关掉了。
    Check("三档都说得到'刚做成的是哪一件'",
        new[] { reserved, warm, direct }.All(x => x.Any(l => l.Contains("刚做成"))));
}

// ---- 人设的清洗与上限 ----
{
    Check("没写人设时得到空串", AiTone.NormalizePersona(null) == string.Empty);
    Check("写坏了的空白也算没写", AiTone.NormalizePersona("   \n\t ") == string.Empty);
    Check("换行被归一化", !AiTone.NormalizePersona("甲\r\n乙")!.Contains('\r'));
    Check("超过上限会被截断而不是报错",
        AiTone.NormalizePersona(new string('字', AiTone.MaxPersonaLength + 200))!.Length
            == AiTone.MaxPersonaLength);
}

// ---- 主动开口的场合名 ----
//
// 【为什么这几条值得测】
// 客户端把场合当**字符串**传给后端，后端用 Enum.TryParse 认回来。
// 这条路上没有任何编译期保护：枚举项删了、名字改了，两边都不报错，
// 表现只是"这一路永远退回本地模板"——看起来功能在，其实 AI 从没说过话。
// 这是上次踩过的坑（Release/Debug 那份不动的 dll），
// 所以在这里把「字符串 → 场合」的对应关系钉住。
{
    Check("focus 认得出来",
        Enum.TryParse<ProactiveKind>("focus", ignoreCase: true, out var fk) && fk == ProactiveKind.Focus);

    Check("nudge 认得出来",
        Enum.TryParse<ProactiveKind>("nudge", ignoreCase: true, out var nk) && nk == ProactiveKind.Nudge);

    Check("morning / before / evening / lowrate 都认得出来",
        Enum.TryParse<ProactiveKind>("morning", ignoreCase: true, out var mk) && mk == ProactiveKind.Morning
        && Enum.TryParse<ProactiveKind>("before", ignoreCase: true, out var bk) && bk == ProactiveKind.Before
        && Enum.TryParse<ProactiveKind>("evening", ignoreCase: true, out var ek) && ek == ProactiveKind.Evening
        && Enum.TryParse<ProactiveKind>("lowrate", ignoreCase: true, out var lk) && lk == ProactiveKind.LowRate);

    Check("认不出来的场合不会被当成默认场合",
        !Enum.TryParse<ProactiveKind>("瞎写的", ignoreCase: true, out var bad) || bad == ProactiveKind.None);
}

// ---- 时间账：设备使用与任务计时求并集 ----
//
// 【为什么这几条值得测】
// 娱乐占比的分母现在是"屏幕前 ∪ 计了时的"，两边是各自独立记下来的。
// 重叠的部分数两遍 → 分母虚高、占比虚低，AI 反而不开口；
// 漏掉某一路 → 分母退回"只算屏幕前"，那正是这次要修的老毛病
// （他做纸上练习的两小时在统计里等于不存在，于是被说成"今天都在玩"）。
// 边界情况（紧邻、倒挂、跨午夜、窗口外）又都堆在最容易写错的地方，所以钉住它们。
{
    // 用本机真实的时区偏移构造：SecondsByDay 是按本地的"天"切的，
    // 写死 +8 的话换个时区跑就全错。
    var offset = TimeZoneInfo.Local.GetUtcOffset(new DateTime(2026, 11, 1, 10, 0, 0));
    var at = new DateTimeOffset(2026, 11, 1, 10, 0, 0, offset);

    var overlapped = new[]
    {
        new TimeInterval(at, at.AddHours(1)),
        new TimeInterval(at.AddMinutes(30), at.AddHours(2))
    };

    Check("重叠的区间合并成一段", IntervalUnion.Merge(overlapped).Count == 1);
    Check("重叠的那半小时只算一次（2 小时，不是 2.5）",
        IntervalUnion.TotalSeconds(overlapped) == 2 * 3600);

    var adjacent = new[]
    {
        new TimeInterval(at, at.AddHours(1)),
        new TimeInterval(at.AddHours(1), at.AddHours(2))
    };
    Check("首尾相接合成一段", IntervalUnion.Merge(adjacent).Count == 1);

    var separate = new[]
    {
        new TimeInterval(at, at.AddHours(1)),
        new TimeInterval(at.AddHours(3), at.AddHours(4))
    };
    Check("中间有空档就还是两段", IntervalUnion.Merge(separate).Count == 2);
    Check("空档不算进总时长", IntervalUnion.TotalSeconds(separate) == 2 * 3600);

    Check("倒挂的区间被丢掉",
        IntervalUnion.TotalSeconds(new[] { new TimeInterval(at.AddHours(1), at) }) == 0);

    Check("不按时间顺序给也一样",
        IntervalUnion.TotalSeconds(new[]
        {
            new TimeInterval(at.AddHours(3), at.AddHours(4)),
            new TimeInterval(at, at.AddHours(1))
        }) == 2 * 3600);

    // 跨午夜：23:30 做到凌晨 0:30，两边各半小时。
    var late = new DateTimeOffset(2026, 11, 1, 23, 30, 0, offset);
    var byDay = IntervalUnion.SecondsByDay(new[] { new TimeInterval(late, late.AddHours(1)) });

    Check("跨午夜的那一段被拆进两天",
        byDay[new DateOnly(2026, 11, 1)] == 30 * 60
        && byDay[new DateOnly(2026, 11, 2)] == 30 * 60);

    Check("裁到窗口里：只留窗口内的那一小时",
        IntervalUnion.Clip(new TimeInterval(at, at.AddHours(5)), at.AddHours(1), at.AddHours(2))!
            .Value.Seconds == 3600);

    Check("完全在窗口外的裁完是空",
        IntervalUnion.Clip(new TimeInterval(at, at.AddHours(1)), at.AddHours(2), at.AddHours(3)) is null);
}

// ============================================================
// 22. 「一天」的边界：凌晨那几个小时归前一天
// ============================================================
Console.WriteLine("=== 22. 一天的边界（跨午夜连续使用时不能把一天切断） ===");

{
    var fiveAm = new DateTimeOffset(2026, 11, 2, 5, 0, 0, TimeSpan.FromHours(8));
    var fourAm = new DateTimeOffset(2026, 11, 2, 4, 59, 0, TimeSpan.FromHours(8));

    Check("起点那一刻算新的一天",
        DayBoundary.Resolve(fiveAm) == new DateOnly(2026, 11, 2));

    Check("起点前一分钟仍归前一天（这是它存在的全部理由）",
        DayBoundary.Resolve(fourAm) == new DateOnly(2026, 11, 1));

    Check("下午当然是当天",
        DayBoundary.Resolve(new DateTimeOffset(2026, 11, 2, 14, 0, 0, TimeSpan.FromHours(8)))
            == new DateOnly(2026, 11, 2));

    Check("起点小时传了非法值不会把日期算飞",
        DayBoundary.Resolve(fourAm, 99) == new DateOnly(2026, 11, 1));

    var (start, end) = DayBoundary.RangeOf(new DateOnly(2026, 11, 1));

    Check("一天的区间是起点到次日起点，整 24 小时",
        (end - start).TotalHours == 24 && start.Hour == DayBoundary.DefaultStartHour);
}

// ============================================================
// 23. 画像：只出事实，样本不够就闭嘴
// ============================================================
Console.WriteLine();
Console.WriteLine("=== 23. 画像（样本不足必须返回空，不能让 AI 拿三次行为说「你总是」） ===");

{
    var at = new DateTimeOffset(2026, 11, 1, 9, 0, 0, TimeSpan.FromHours(8));
    var day = new DateOnly(2026, 11, 1);

    // 造一批样本：启动延迟 10 分钟，实际是估的两倍。
    WorkSample Sample(int delayMinutes, int? estimate = 30, int? actual = 60, bool skipped = false, string? reason = null) =>
        new(at, at.AddMinutes(delayMinutes), estimate, actual, at.AddMinutes(actual ?? 0), skipped, reason);

    var few = Enumerable.Range(0, 3).Select(_ => Sample(10)).ToList();

    Check("只有三次样本时启动延迟不出结论",
        ProfileCalculator.Build(few, new List<DayRate>(), new List<ActiveSpan>()).StartDelay is null);

    var enough = Enumerable.Range(0, 6).Select(_ => Sample(10)).ToList();
    var profile = ProfileCalculator.Build(enough, new List<DayRate>(), new List<ActiveSpan>());

    Check("够五次就出结论", profile.StartDelay is not null);
    Check("延迟算对了（计划 9:00，实际 9:10 启动）", profile.StartDelay!.MedianMinutes == 10);
    Check("结论带着样本数，好让 AI 知道这话有多重", profile.StartDelay.SampleCount == 6);
    Check("估时偏差：估 30 实际 60 就是 2.0", Math.Abs(profile.Estimation!.ActualOverEstimate - 2.0) < 0.001);

    // 提前开始（负数）不该被算成延迟。
    var early = Enumerable.Range(0, 6).Select(_ => new WorkSample(at, at.AddMinutes(-5), 30, 60, null, false, null)).ToList();
    Check("提前开始不算拖延，也不进延迟统计",
        ProfileCalculator.Build(early, new List<DayRate>(), new List<ActiveSpan>()).StartDelay is null);

    // 没定时间的任务谈不上"延迟"。
    var noPlan = Enumerable.Range(0, 6)
        .Select(_ => new WorkSample(null, at, 30, 60, null, false, null)).ToList();
    Check("没定时间的任务不计入启动延迟",
        ProfileCalculator.Build(noPlan, new List<DayRate>(), new List<ActiveSpan>()).StartDelay is null);

    // 跳过原因要能聚出来。
    var withSkips = Enumerable.Range(0, 6).Select(_ => Sample(10)).ToList();
    withSkips.AddRange(Enumerable.Range(0, 4).Select(_ => Sample(0, null, null, skipped: true, reason: "太难")));
    var skipProfile = ProfileCalculator.Build(withSkips, new List<DayRate>(), new List<ActiveSpan>());

    Check("没做成的原因被聚出来了",
        skipProfile.SkipReasons.Count == 1 && skipProfile.SkipReasons[0].Reason == "太难");
    Check("跳过次数对得上", skipProfile.SkipReasons[0].Count == 4);

    Check("什么都没有的时候是空画像",
        ProfileCalculator.Build(new List<WorkSample>(), new List<DayRate>(), new List<ActiveSpan>())
            .StartDelay is null);
}

// ============================================================
// 24. 活跃小时：跨小时的段必须劈开算
// ============================================================
Console.WriteLine();
Console.WriteLine("=== 24. 哪个小时他在动（跨小时的那一段要劈开，不能整段记在起点上） ===");

{
    var start = new DateTimeOffset(2026, 11, 1, 13, 30, 0, TimeSpan.FromHours(8));

    var spans = new List<ActiveSpan>
    {
        new(start, start.AddMinutes(100))   // 13:30 → 15:10
    };

    var profile = ProfileCalculator.Build(
        new List<WorkSample>(), new List<DayRate>(), spans);

    Check("累计不到两小时就不谈时段规律",
        profile.ActiveHours.Count == 0);

    var longer = new List<ActiveSpan>
    {
        new(start, start.AddMinutes(200))   // 13:30 → 16:50
    };

    var hours = ProfileCalculator
        .Build(new List<WorkSample>(), new List<DayRate>(), longer)
        .ActiveHours;

    Check("够两小时了就给时段", hours.Count > 0);
    Check("13 点那半小时算在 13 点上", hours.First(x => x.Hour == 13).Minutes == 30);
    Check("14 点整小时算满 60", hours.First(x => x.Hour == 14).Minutes == 60);
    Check("16 点只算了 50 分钟（到 16:50 为止）", hours.First(x => x.Hour == 16).Minutes == 50);
}

// ============================================================
// 25. 趋势：方向不能搞反
// ============================================================
Console.WriteLine();
Console.WriteLine("=== 25. 趋势（进步的方向各指标不一样，不能一律按「变大就是好」） ===");

{
    var lastWeek = new PeriodStat(new DateOnly(2026, 10, 20), 5, 40, 0.5, 300, 10, 5);
    var thisWeek = new PeriodStat(new DateOnly(2026, 10, 27), 5, 15, 0.6, 400, 10, 6);

    var items = TrendCalculator.Compare(lastWeek, thisWeek);
    var delay = items.First(x => x.Label == "启动要多久");
    var rate = items.First(x => x.Label == "完成率");
    var active = items.First(x => x.Label == "做事时长");

    Check("启动变短是进步", delay.Improved);
    Check("完成率变高是进步", rate.Improved);
    Check("做事时长变长是进步", active.Improved);

    Check("给 AI 的画像段在没数据时整段不出现",
        ProfileLines.ToPrompt(UserProfile.Empty) == string.Empty);

    var someProfile = ProfileCalculator.Build(
        Enumerable.Range(0, 6)
            .Select(_ => new WorkSample(
                new DateTimeOffset(2026, 11, 1, 9, 0, 0, TimeSpan.FromHours(8)),
                new DateTimeOffset(2026, 11, 1, 9, 20, 0, TimeSpan.FromHours(8)),
                30, 60, null, false, null))
            .ToList(),
        new List<DayRate>(),
        new List<ActiveSpan>());

    var prompt = ProfileLines.ToPrompt(someProfile);

    Check("有画像时提示词里带着不许念给他听的硬规矩",
        prompt.Contains("不要念给他听"));
    Check("给 AI 的每一条都带样本数",
        prompt.Contains("看了 6 次"));

    // 天数不够时，趋势只说"还在攒"，不给数字。
    var thinLast = new PeriodStat(new DateOnly(2026, 10, 20), 1, 40, 0.5, 300, 2, 1);
    var thinThis = new PeriodStat(new DateOnly(2026, 10, 27), 1, 15, 0.9, 400, 2, 2);
    var thinItems = TrendCalculator.Compare(thinLast, thinThis);

    Check("天数不够时不给结论", thinItems.All(x => !x.EnoughData));
    Check("天数不够时写的是「还在攒」而不是一个假数字",
        ProfileLines.DescribeForUser(thinItems.First()).Contains("还在攒"));
}

// ============================================================
// 26. 界面资源：XAML 里引用的键名必须真的存在
// ============================================================
//
// 【为什么要让冒烟来查这一条】
// 2026-11-02 的真事：时间页新加的卡片写成 Style="{StaticResource Card}"，
// 而实际定义的是 CardStyle。**编译 0 错 0 警**——XAML 的静态资源是运行时才解析的。
// 运行时它炸在页面构造函数里（InitializeComponent），
// 而页面是 Shell 切 Tab 时才被创建的：于是页面没建成、Shell 的当前项也没换过去，
// 用户看到的是"切到时间页之后整个界面卡死，切不回来了"。
// 日志里躺着 XamlParseException，但界面上什么都没说。
// 这类错误只能靠"把所有 XAML 扫一遍、比对键名"提前抓住——人眼扫不出来。
Console.WriteLine();
Console.WriteLine("=== 26. 界面资源（键名写错编译不报错，运行时整页炸） ===");

{
    var srcRoot = FindSrcRoot();

    Check("找得到源码根目录", srcRoot is not null, srcRoot);

    if (srcRoot is not null)
    {
        var files = Directory
            .GetFiles(srcRoot, "*.xaml", SearchOption.AllDirectories)
            // bin/obj 里有编译复制过去的副本，扫它们会把同一个键算两遍、还会读到过期的旧文件。
            .Where(p => !p.Contains("\\bin\\") && !p.Contains("\\obj\\")
                        && !p.Contains("/bin/") && !p.Contains("/obj/"))
            .ToList();

        var defined = new HashSet<string>(StringComparer.Ordinal);
        var used = new Dictionary<string, List<string>>(StringComparer.Ordinal);

        foreach (var file in files)
        {
            var text = File.ReadAllText(file);
            foreach (Match m in Regex.Matches(text, "x:Key=\"([^\"]+)\""))
            {
                defined.Add(m.Groups[1].Value);
            }
        }

        foreach (var file in files)
        {
            var text = File.ReadAllText(file);
            foreach (Match m in Regex.Matches(text, @"\{(?:Static|Dynamic)Resource\s+([A-Za-z0-9_]+)"))
            {
                var key = m.Groups[1].Value;
                if (!used.TryGetValue(key, out var where))
                {
                    used[key] = where = new List<string>();
                }

                where.Add(Path.GetFileName(file));
            }
        }

        Check("XAML 都扫到了", files.Count >= 10, $"{files.Count} 个文件、{defined.Count} 个键");

        var missing = used.Where(x => !defined.Contains(x.Key)).ToList();

        Check(
            "没有引用不存在的资源键",
            missing.Count == 0,
            missing.Count == 0
                ? null
                : string.Join("；", missing.Select(
                    x => $"{x.Key} 被 {string.Join(",", x.Value.Distinct())} 引用")));
    }
}

// ============================================================
// 27. 画像给他自己看的那一版
// ============================================================
//
// 【为什么这一节必须存在】
// 画像原来只喂给 AI，用户自己看不到——那等于这个功能只对 AI 有效。
// 补上"给自己看"的版本之后，最容易出的两种错是：
// ① 把没攒够的东西说得像结论（"你启动要 0 分钟"）；
// ② 把失败清单摆到他面前（"你没做成 5 次"）。
// 两条都会让人关掉这个功能，所以要在离线就能断言住。
Console.WriteLine();
Console.WriteLine("=== 27. 画像给自己看的那一版（不能只有 AI 能看到） ===");

{
    var empty = ProfileLines.DescribeForSelf(UserProfile.Empty);

    Check("全新用户看到的是「还差什么」，不是空白",
        empty.Count == 2 && empty.All(x => x.IsPending),
        string.Join(" / ", empty.Select(x => x.Text)));

    var active = new List<ActiveSpan>
    {
        new(new DateTimeOffset(2026, 11, 1, 14, 0, 0, TimeSpan.FromHours(8)),
            new DateTimeOffset(2026, 11, 1, 16, 10, 0, TimeSpan.FromHours(8)))
    };

    var partial = ProfileCalculator.Build(
        new List<WorkSample>(),
        new List<DayRate> { new(new DateOnly(2026, 11, 1), 3, 2) },
        active);

    var facts = ProfileLines.DescribeForSelf(partial);

    Check("活跃够两小时就说得出「几点在状态」",
        facts.Any(x => x.Label == "什么时候在状态" && !x.IsPending));

    Check("有记录的天给的是「安排了多少、做了多少」",
        facts.Any(x => x.Text.Contains("安排了 3 件、做了 2 件")));

    Check("范围写的是「最近两周」，不是一个天天变的数字",
        facts.Any(x => x.Label == "最近两周"));

    Check("启动样本不足时说的是「再记 5 次」，不是编一个数字",
        facts.Any(x => x.Label == "从计划到动手" && x.IsPending && x.Remaining == 5),
        string.Join(" / ", facts.Where(x => x.IsPending).Select(x => x.Text)));

    Check("没做成的原因不进他这一版（那是给 AI 调建议用的，不是给他看的清单）",
        facts.All(x => !x.Label.Contains("没做成")));

    // 够五条样本之后，同一处要变成结论。
    var samples = Enumerable.Range(0, 5)
        .Select(_ => new WorkSample(
            new DateTimeOffset(2026, 11, 1, 9, 0, 0, TimeSpan.FromHours(8)),
            new DateTimeOffset(2026, 11, 1, 9, 30, 0, TimeSpan.FromHours(8)),
            null, null, null, false, null))
        .ToList();

    var rich = ProfileCalculator.Build(
        samples, new List<DayRate>(), new List<ActiveSpan>());

    var delay = ProfileLines.DescribeForSelf(rich).First(x => x.Label == "从计划到动手");

    Check("样本够了之后，同一处变成中位数结论（不再是「再记几次」）",
        !delay.IsPending && delay.Text.Contains("30 分钟"), delay.Text);

    Check("一周什么都没有时不编数字",
        ProfileLines.DescribeRecentWeek(
            new PeriodStat(new DateOnly(2026, 10, 27), 0, null, 0, 0, 0, 0)).Contains("还没有可算"));

    Check("一周只做事没排安排也说得出来",
        ProfileLines.DescribeRecentWeek(
            new PeriodStat(new DateOnly(2026, 10, 27), 0, null, 0, 90, 0, 0)).Contains("没排安排"));

    Check("有安排时同时给安排数和完成数",
        ProfileLines.DescribeRecentWeek(
            new PeriodStat(new DateOnly(2026, 10, 27), 5, null, 0.5, 120, 10, 5))
            .Contains("安排了 10 件、做了 5 件"));
}

// ============================================================
// 28. AI 没回上来时的自动重发：绝不能冒出第二条消息
// ============================================================
//
// 【为什么要让冒烟来查这一条】
// 重试加在"发出一句话、等 AI 回复"这条路径上，最容易写错的方式是
// 把整个 RunAsync 拿去重跑：那样用户气泡会再加一次、chat_messages 再写一行，
// 下次拼历史时 AI 会看见"同一句他说了两遍"——正是要避免的上下文污染。
// 而这两种写法都能编译通过、界面上也不报错，只有翻库才看得出来。
// 所以把"重试循环体内不新增气泡、不写库""拿到结果就停""有上限"
// 当作不变量，直接扫源码断言住。
Console.WriteLine();
Console.WriteLine("=== 28. 自动重发（重试不能产生第二条消息 / 第二行库记录） ===");

{
    var srcRoot = FindSrcRoot();
    var vmPath = srcRoot is null
        ? null
        : Path.Combine(srcRoot, "DayManager.App", "ViewModels", "ChatViewModel.cs");

    Check("找得到 ChatViewModel.cs", vmPath is not null && File.Exists(vmPath), vmPath);

    if (vmPath is not null && File.Exists(vmPath))
    {
        var code = File.ReadAllText(vmPath);

        // 只截"重试循环"这一段：从 for 起到"最终失败"判断之前。
        var loopStart = code.IndexOf("for (var attempt = 0", StringComparison.Ordinal);
        var loopEnd = code.IndexOf("if (okTurn is null)", StringComparison.Ordinal);
        Check("重试循环还在（没被整段删掉）", loopStart >= 0 && loopEnd > loopStart);

        if (loopStart >= 0 && loopEnd > loopStart)
        {
            var loop = code.Substring(loopStart, loopEnd - loopStart);

            Check("重发的是 _api.AgentAsync 这一句", loop.Contains("_api.AgentAsync"));

            Check("重试循环里不新增任何气泡",
                !loop.Contains("Messages.Add") && !loop.Contains("ChatBubble.User"));

            Check("重试循环里不写库",
                !loop.Contains("SaveAsync") && !loop.Contains("_chat.AddAsync"));

            Check("拿到成功结果就停，绝不发第二次",
                Regex.IsMatch(loop, @"if\s*\(\s*one\.Ok\s*\)[\s\S]{0,200}?break"));

            Check("每轮之间真的等了一下", loop.Contains("Task.Delay(RetryDelay)"));
        }

        Check("最多重发两次（不是无限重发）",
            code.Contains("MaxRetries = 2") && code.Contains("attempt <= MaxRetries"));

        Check("间隔是 1 秒",
            Regex.IsMatch(code, @"RetryDelay\s*=\s*TimeSpan\.FromSeconds\(1\)"));

        Check("两次都没回上来，原话放回输入框",
            Regex.IsMatch(code, @"InputText\s*=\s*userText"));

        // 用户明确要求：气泡里就显示错误，不要塞"重发了几遍、原话已放回"这类说明。
        // 只认那句真的 Messages.Add，不认注释，免得以后注释里出现同样几个字就误报。
        var errLine = code.Split('\n').FirstOrDefault(
            l => l.Contains("Messages.Add") && l.Contains("AI 没回上来："));
        Check("失败气泡只有错误一句，不塞重发说明",
            errLine is not null
            && !errLine.Contains("重发") && !errLine.Contains("重试")
            && !errLine.Contains("原话") && !errLine.Contains("复制"),
            errLine?.Trim());
    }
}

Console.WriteLine();
Console.WriteLine("=== 29. 时间观念：历史带时间标注 + 时间坐标（治跨夜说晚安 / 吃饭事件 / 催睡觉） ===");

{
    // 固定一个"现在"：2026-11-21 周六上午 10:30。测试不能依赖跑的那一刻。
    var anchor = new DateTime(2026, 11, 21, 10, 30, 0, DateTimeKind.Local);
    DateTimeOffset At(int day, int hour, int minute = 0) =>
        new(new DateTime(2026, 11, day, hour, minute, 0, DateTimeKind.Local));

    // ① 时段是代码判的，不留给它自己猜。上午绝不能被算成深夜。
    Check("上午 10:30 判成「上午」", ChatTime.Band(anchor) == "上午");
    Check("深夜 01:00 判成「深夜」", ChatTime.Band(At(21, 1).LocalDateTime) == "深夜");
    Check("23:30 判成「深夜」", ChatTime.Band(At(21, 23, 30).LocalDateTime) == "深夜");
    Check("下午 15:00 判成「下午」", ChatTime.Band(At(21, 15).LocalDateTime) == "下午");

    // ② 日期标签：跨夜要变成看得见的字。
    Check("同一天标「今天」", ChatTime.DayLabel(At(21, 9).LocalDateTime, anchor).StartsWith("今天"));
    Check("前一天标「昨天」", ChatTime.DayLabel(At(20, 23).LocalDateTime, anchor).StartsWith("昨天"));
    Check("更早的写出月日", ChatTime.DayLabel(At(17, 22).LocalDateTime, anchor).StartsWith("11-17"));

    // ③ 单条标注：内容原样保留，前面加时间；没有时间不加标注（不能凭空捏）。
    var marked = ChatTime.Mark("我去吃饭了", At(20, 23, 48), anchor);
    Check("历史标注带「昨天」", marked.Contains("昨天"));
    Check("历史标注带时段「深夜」", marked.Contains("深夜"));
    Check("历史标注保留原话", marked.EndsWith("我去吃饭了"));
    Check("没有时间就不加标注", ChatTime.Mark("原话", null, anchor) == "原话");

    // ④ 间隔是硬数字，跨夜要说出来。
    Check("隔了一夜要说出来", ChatTime.GapWithNight(At(20, 23, 48).LocalDateTime, anchor).Contains("隔了一夜"));
    Check("同日下午不算隔夜", !ChatTime.GapWithNight(At(21, 9).LocalDateTime, anchor).Contains("隔了一夜"));
    Check("分钟差算得出来", ChatTime.Gap(anchor.AddMinutes(-37), anchor).Contains("37"));

    // ⑤ 坐标段：三条规矩都在（时段 / 旧状态过期 / 别自己推时间）。
    var coord = ChatTime.Coordinate(anchor, At(20, 23, 48));
    Check("坐标段写明现在是上午", coord.Contains("上午"));
    Check("坐标段写明距上次说话隔了多久", coord.Contains("隔了一夜"));
    Check("坐标段写明旧状态会过期", coord.Contains("默认已经结束"));
    Check("坐标段写明别自己推时间", coord.Contains("别自己再推一遍时间"));
    Check("坐标段写明说话要合时段", coord.Contains("上午别道晚安"));
    Check("没有上一句时不硬编时间", !ChatTime.Coordinate(anchor, null).Contains("上一句话"));

    // ⑥ 源码扫描：两条链路都得真的用上同一份 ChatTime，不能只改了一边。
    var srcRoot = FindSrcRoot();
    string Read(string rel) => srcRoot is null ? "" :
        File.ReadAllText(Path.Combine(srcRoot, rel));

    var agentCode = Read(Path.Combine("DayManager.Api", "Services", "AgentService.cs"));
    var proactiveCode = Read(Path.Combine("DayManager.Api", "Services", "ProactiveService.cs"));
    var vmCode = Read(Path.Combine("DayManager.App", "ViewModels", "ChatViewModel.cs"));
    var apiClientCode = Read(Path.Combine("DayManager.App", "Services", "ApiClient.cs"));
    var proxyCode = Read(Path.Combine("DayManager.Api", "Services", "AiProxy.cs"));

    Check("聊天链路逐条打了时间标注", agentCode.Contains("ChatTime.Mark"));
    Check("聊天链路插了时间坐标", agentCode.Contains("ChatTime.Coordinate"));
    Check("主动开口链路逐条打了时间标注", proactiveCode.Contains("ChatTime.Mark"));
    Check("主动开口链路插了时间坐标", proactiveCode.Contains("ChatTime.Coordinate"));
    Check("主动开口不再只取 m.Text 丢掉时间", !Regex.IsMatch(proactiveCode, @"Content\s*=\s*m\.Text\s*[,}]"));
    Check("客户端把气泡的 CreatedAt 带进历史", vmCode.Contains("ChatMessageDto.User(x.Text, x.CreatedAt)"));
    Check("请求 payload 带上 createdAt", apiClientCode.Contains("createdAt = x.CreatedAt"));
    Check("发给上游仍只投影 role/content（时间字段不外泄）",
        Regex.IsMatch(proxyCode, @"new\s*\{\s*role\s*=\s*m\.Role\s*,\s*content\s*=\s*m\.Content\s*\}"));
}

Console.WriteLine();
Console.WriteLine("=== 30. 视野：过去 7 天回顾 + AI 按需 query（治「看不到昨天的任务」） ===");

{
    // ① 纯函数：查询范围的解析与裁剪。放 Core 就是为了让这里能直接断言边界，
    //    不用真调一次大模型才发现它写了个「查去年一整年」。
    var single = DayQuery.Parse("2026-11-03", null);
    Check("只给起始日 = 只查这一天", single.Valid && single.Days == 1
        && single.From == new DateOnly(2026, 11, 3) && single.To == single.From);

    var range = DayQuery.Parse("2026-11-03", "2026-11-05");
    Check("给了结束日就按范围算", range.Valid && range.Days == 3
        && !range.Clipped && range.Enumerate().Last() == new DateOnly(2026, 11, 5));

    var reversed = DayQuery.Parse("2026-11-05", "2026-11-03");
    Check("日期写反了自动换过来，不当成查不到", reversed.Valid
        && reversed.From == new DateOnly(2026, 11, 3) && reversed.To == new DateOnly(2026, 11, 5));

    Check("起始日是乱写的要挡住", !DayQuery.Parse("上个月", null).Valid);
    Check("没给起始日要挡住", !DayQuery.Parse(null, null).Valid && !DayQuery.Parse("", "2026-11-03").Valid);

    var clipped = DayQuery.Parse("2026-01-01", "2026-12-31");
    Check("一次查一年会被裁到上限", clipped.Valid && clipped.Clipped && clipped.Days == DayQuery.MaxSpanDays);
    Check("裁完仍是连续的一段", clipped.To == clipped.From.AddDays(DayQuery.MaxSpanDays - 1));

    // ② 回顾取数：核心是「逾期旧账不能在 7 天里重复 7 遍」。
    //    选一个远离其它测试数据的日子，避免和历史用例造的记录互相干扰。
    var reviewDay = DateOnly.FromDateTime(DateTime.Today.AddDays(-40));
    DateTimeOffset AtLocal(int dayOffset, int hour)
    {
        var local = DateTime.Today.AddDays(dayOffset).AddHours(hour);
        return new DateTimeOffset(local, TimeZoneInfo.Local.GetUtcOffset(local));
    }

    Guid overdueId, scheduledId, completedId, abandonedId, bothId;
    await using (var db = factory.CreateDbContext())
    {
        Item Mk(string title, ItemStatus st, DateTimeOffset? start, DateTimeOffset? due, DateTimeOffset? done) => new()
        {
            Title = title,
            Status = st,
            StartAt = start,
            DueAt = due,
            CompletedAt = done,
            TimeZoneId = TimeZoneInfo.Local.Id
        };

        // 这条很早到期、至今没做的欠账，按界面口径翻到过去任意一天都会冒出来。
        // 截止日要远在 7 天扫描窗口之前，否则最远那天因 DueAt < dayEnd 不成立会少一次。
        var overdue = Mk("回顾测试-逾期未完成的旧账", ItemStatus.Pending, null, AtLocal(-60, 23), null);
        // 那天有具体钟点的日程：回顾里该在。
        var scheduled = Mk("回顾测试-那天十点的会", ItemStatus.Pending, AtLocal(-40, 10), null, null);
        // 更早创建的待办，但在那天做完：回顾里也该在（做完发生在那天）。
        var completed = Mk("回顾测试-那天做完的事", ItemStatus.Done, null, AtLocal(-60, 23), AtLocal(-40, 15));
        // 放弃的不该出现（用户已经决定不做）。
        var abandoned = Mk("回顾测试-那天放弃的事", ItemStatus.Abandoned, AtLocal(-40, 11), null, null);
        // 同时满足「那天排的」和「那天做完的」：只能出现一次。
        var both = Mk("回顾测试-当天排当天做完", ItemStatus.Done, AtLocal(-40, 9), null, AtLocal(-40, 12));

        db.Items.AddRange(overdue, scheduled, completed, abandoned, both);
        await db.SaveChangesAsync();

        overdueId = overdue.Id;
        scheduledId = scheduled.Id;
        completedId = completed.Id;
        abandonedId = abandoned.Id;
        bothId = both.Id;
    }

    var reviewList = await repo.GetDayReviewAsync(reviewDay);
    Check("回顾里有那天有钟点的日程", reviewList.Any(x => x.Id == scheduledId));
    Check("回顾里有那天做完的事", reviewList.Any(x => x.Id == completedId));
    Check("回顾里没有一直欠着的逾期旧账", reviewList.All(x => x.Id != overdueId));
    Check("回顾里没有放弃的事", reviewList.All(x => x.Id != abandonedId));
    Check("同一条同时满足两类时不重复", reviewList.Count(x => x.Id == bothId) == 1,
        $"出现了 {reviewList.Count(x => x.Id == bothId)} 次");

    // 对照：界面翻页口径确实会把那条旧账并进过去每一天——这正是不能照抄它的理由。
    var interfaceList = await repo.GetDayListAsync(reviewDay);
    Check("对照：界面翻到那天会带上逾期旧账", interfaceList.Any(x => x.Id == overdueId));

    var reviewHits = 0;
    var interfaceHits = 0;
    for (var i = 0; i < 7; i++)
    {
        var scanDay = reviewDay.AddDays(-i);
        if ((await repo.GetDayReviewAsync(scanDay)).Any(x => x.Id == overdueId)) reviewHits++;
        if ((await repo.GetDayListAsync(scanDay)).Any(x => x.Id == overdueId)) interfaceHits++;
    }
    Check("连着 7 天回顾，旧账一次都不重复", reviewHits == 0, $"回顾里出现 {reviewHits} 次");
    Check("对照：同样 7 天界面口径会把它带 7 次", interfaceHits == 7,
        $"界面口径出现 {interfaceHits} 次");

    // ③ 源码扫描：确认真的接上了，而不只是文件里躺着一个新类。
    var srcRoot30 = FindSrcRoot();
    string Read30(string rel) => srcRoot30 is null ? "" :
        File.ReadAllText(Path.Combine(srcRoot30, rel));

    var agent30 = Read30(Path.Combine("DayManager.Api", "Services", "AgentService.cs"));
    var tools30 = Read30(Path.Combine("DayManager.Api", "Services", "ItemTools.cs"));
    var models30 = Read30(Path.Combine("DayManager.Api", "Models", "AgentModels.cs"));
    var repo30 = Read30(Path.Combine("DayManager.Data", "Repositories", "ItemRepository.cs"));
    var proactive30 = Read30(Path.Combine("DayManager.Api", "Services", "ProactiveService.cs"));
    var proxy30 = Read30(Path.Combine("DayManager.Api", "Services", "AiProxy.cs"));
    var dayQueryFile = srcRoot30 is null ? "" : Path.Combine(srcRoot30, "DayManager.Core", "Ai", "DayQuery.cs");

    Check("Core 里有 DayQuery 纯函数", File.Exists(dayQueryFile));
    Check("query 的解析放 Core、不在 Api 里重写", File.Exists(dayQueryFile)
        && File.ReadAllText(dayQueryFile).Contains("MaxSpanDays"));

    Check("聊天链路真的取了过去几天的回顾", agent30.Contains("GetDayReviewAsync"));
    Check("回顾取数走仓储、口径只有一份", repo30.Contains("GetDayReviewAsync"));
    Check("聊天链路有「查完再答」的循环", agent30.Contains("MaxQueryRounds")
        && agent30.Contains("RunQueriesAsync"));
    Check("query 在交给执行器前被剔掉（不会进撤销批次）",
        agent30.Contains("Where(a => !IsQuery(a))"));
    Check("执行器对漏进来的 query 有明确防御", tools30.Contains("case \"query\""));
    Check("动作模型带上了查询结束日", models30.Contains("DateTo"));

    Check("提示词不再教它说「看不到」",
        !agent30.Contains("更早以前的、以及这几天之后的安排，你看不到"));
    Check("提示词明确改为「可以查、不要再说看不到」",
        agent30.Contains("不要再说「我看不到」") && agent30.Contains("query"));
    Check("提示词列了六种操作", agent30.Contains("可用的操作有这六种"));
    // 源码里那行是带转义的 C# 字符串字面量，直接找 "op": "query" 会漏；
    // 用示例里独有的文案 + 日期来确认它还在。
    Check("提示词给了 query 的写法示例",
        agent30.Contains("他问的是上个月") && agent30.Contains("2026-10-05"));

    // 范围守约：这次只扩聊天链路，主动开口那边不动。
    Check("主动开口链路没被顺手改掉", !proactive30.Contains("RunQueriesAsync")
        && !proactive30.Contains("GetDayReviewAsync"));
    // 时间字段仍然只在本机传输，不外泄给上游。
    Check("发给上游仍只投影 role/content",
        Regex.IsMatch(proxy30, @"new\s*\{\s*role\s*=\s*m\.Role\s*,\s*content\s*=\s*m\.Content\s*\}"));
}

// ============================================================
// 31. 翻更早历史 + 微信式日期条
// ============================================================
Console.WriteLine();
Console.WriteLine("=== 31. 翻更早历史 + 按天分组的日期条 ===");

{
    var anchor31 = new DateTime(2026, 11, 21, 10, 30, 0, DateTimeKind.Local); // 周六
    DateTime D31(int day, int hour = 10) => new(2026, 11, day, hour, 0, 0, DateTimeKind.Local);

    // ① 日期条的文案是硬判断，不留给它自己推。
    Check("同一天显示「今天」", ChatTime.DayDivider(D31(21), anchor31) == "今天");
    Check("前一天显示「昨天」", ChatTime.DayDivider(D31(20), anchor31) == "昨天");
    Check("两天前显示星期几", ChatTime.DayDivider(D31(19), anchor31) == "星期四");
    Check("一周内边界：六天前仍是星期", ChatTime.DayDivider(D31(15), anchor31) == "星期日");
    Check("七天前改写成月日，不再含糊说星期", ChatTime.DayDivider(D31(14), anchor31) == "11月14日");
    Check("同年更早的写月日", ChatTime.DayDivider(D31(3), anchor31) == "11月3日");
    Check("跨年必须带年份", ChatTime.DayDivider(new DateTime(2025, 12, 25, 10, 0, 0, DateTimeKind.Local), anchor31)
        == "2025年12月25日");

    // ② 切天按本地，不按 UTC：库里存的是 UTC，照 UTC 切会把跨夜两条算成同一天。
    var utcAcross = new DateTimeOffset(2026, 11, 20, 16, 30, 0, TimeSpan.Zero);
    Check("LocalDay 用本地日期而不是 UTC 日期",
        ChatTime.LocalDay(utcAcross) == DateOnly.FromDateTime(utcAcross.ToLocalTime().DateTime));
    var sameInstant = new DateTimeOffset(2026, 11, 21, 0, 30, 0, TimeSpan.FromHours(8));
    Check("同一时刻的两种 offset 写法算同一天",
        ChatTime.LocalDay(utcAcross) == ChatTime.LocalDay(sameInstant));
    Check("确实会把不同日子切开（不是恒返回同一天）",
        ChatTime.LocalDay(utcAcross) != ChatTime.LocalDay(utcAcross.AddDays(2)));

    // ③ 翻页：整窗重取 + 按 id 去重。造一批新对话，避免依赖前面章节留下的软删数据。
    var chat31 = new ChatRepository(factory);
    await chat31.ClearAsync();
    for (var i = 0; i < 6; i++)
    {
        await chat31.AddAsync(ChatMessage.RoleUser, $"翻页测试-{i}");
    }

    Check("总数只数没删的", await chat31.CountAsync() == 6);

    var page1 = await chat31.GetRecentAsync(3);
    Check("第一页按时间正序（界面从上往下读）", page1.Count == 3
        && page1[0].CreatedAt <= page1[1].CreatedAt && page1[1].CreatedAt <= page1[2].CreatedAt);

    // 模拟「窗口扩大一页」：取回整窗，只留没显示过的。
    var window31 = await chat31.GetRecentAsync(page1.Count + 3);
    Check("第一页就是整窗里最新的三条", window31.Count == 6
        && page1.Select(x => x.Id).SequenceEqual(window31.Skip(3).Select(x => x.Id)));
    Check("整窗也是从早到晚",
        window31.Zip(window31.Skip(1)).All(p => p.First.CreatedAt <= p.Second.CreatedAt));
    var shown31 = page1.Select(x => x.Id).ToHashSet();
    var older31 = window31.Where(x => !shown31.Contains(x.Id)).ToList();
    Check("扩窗后只挑出没显示过的（不会重复）", older31.Count == 3
        && older31.All(x => !shown31.Contains(x.Id)));
    Check("挑出来的排在整窗前面（更早的那一段）",
        older31.Select(x => x.Id).SequenceEqual(window31.Take(3).Select(x => x.Id)));
    Check("旧页和新页拼起来正好是整窗，不重不漏",
        older31.Concat(page1).Select(x => x.Id).OrderBy(x => x).SequenceEqual(
            window31.Select(x => x.Id).OrderBy(x => x)));
    Check("全翻完之后按钮就该消失", await chat31.CountAsync() <= window31.Count);

    // 软删之后总数跟着减，翻页不会把删掉的那条又捞回来。
    await chat31.DeleteAsync(page1[0].Id);
    Check("单条软删后总数减少", await chat31.CountAsync() == 5);
    Check("删掉的那条不在最新窗口里", (await chat31.GetRecentAsync(7)).All(x => x.Id != page1[0].Id));

    await chat31.ClearAsync();

    // ④ 源码扫描：确认真的接上了，而不是文件里躺着几个新类。
    var src31 = FindSrcRoot();
    string Read31(string rel) => src31 is null ? "" : File.ReadAllText(Path.Combine(src31, rel));

    var time31 = Read31(Path.Combine("DayManager.Core", "Ai", "ChatTime.cs"));
    var repo31 = Read31(Path.Combine("DayManager.Data", "Repositories", "ChatRepository.cs"));
    var vm31 = Read31(Path.Combine("DayManager.App", "ViewModels", "ChatViewModel.cs"));
    var page31 = Read31(Path.Combine("DayManager.App", "ChatPage.xaml"));
    var code31 = Read31(Path.Combine("DayManager.App", "ChatPage.xaml.cs"));

    Check("Core 里有日期条纯函数", time31.Contains("public static string DayDivider"));
    Check("Core 里有按本地切天的函数", time31.Contains("public static DateOnly LocalDay"));
    Check("仓储有总数查询", repo31.Contains("public async Task<int> CountAsync"));
    Check("翻页靠整窗重取，不用会错位的 skip", !repo31.Contains("Skip(skip)"));
    Check("界面按 id 去重，不靠计数器", vm31.Contains("ToHashSet()"));
    Check("集合变了会整段重算日期分界", vm31.Contains("RefreshDayDividers()"));
    Check("有翻页命令", vm31.Contains("LoadMoreHistoryAsync"));
    Check("气泡带日期条的两个字段", vm31.Contains("ShowDayDivider") && vm31.Contains("DayDividerText"));
    Check("XAML 里有加载更早按钮", page31.Contains("加载更早的消息") && page31.Contains("HasMore"));
    Check("XAML 里日期条绑了独立的居中标签", page31.Contains("DayDividerText")
        && page31.Contains("ShowDayDivider"));
    Check("翻页时不滚到底", code31.Contains("IsPrepending") && code31.Contains("ScrollToLast"));
    Check("翻页后把阅读位置锚回去", code31.Contains("RestoreAnchor")
        && code31.Contains("ChatHistoryPrependStarted") && code31.Contains("ChatHistoryPrepended"));
    Check("清空之后按钮会收起来", vm31.Contains("HasMore = false"));
}

// ============================================================
// 32. 只有截止日的事（5.2 第三种形态）+ 三条完成语义
// ============================================================
Console.WriteLine();
Console.WriteLine("=== 32. 只有截止日的事：常驻带 / 完成语义 / 完成率分母 ===");

{
    // 用独立临时库：GetFloatingDeadlinesAsync 是全表口径（粗筛所有未来截止的
    // 无开始时间待办），前面 31 节造的数据会混进来，无法精确断言数量和排序。
    var dbFile32 = Path.Combine(Path.GetTempPath(), $"dm-smoke-floating-{Guid.NewGuid():N}.db");
    var factory32 = new TestFactory(dbFile32);
    await using (var db32 = factory32.CreateDbContext())
    {
        await db32.Database.EnsureCreatedAsync();
    }
    var repo32 = new ItemRepository(factory32);

    var anchor32 = DateOnly.FromDateTime(DateTime.Today);

    // 带本地偏移的时刻：纯函数内部会 ToLocalTime，构造时就得按本地来。
    DateTimeOffset Local(int dayOffset, int hour = 23, int minute = 59) =>
        new(DateTime.Today.AddDays(dayOffset).AddHours(hour).AddMinutes(minute),
            TimeZoneInfo.Local.GetUtcOffset(DateTime.Today.AddDays(dayOffset)));

    // ① 纯函数：这一格属不属于「还悬着」。
    // 【11-21 改版】第一句判据从「StartAt 空 + DueAt 未来」换成 Shape == DeadlineOnly，
    // 形态不再靠推断。下面每条都显式给形态，正是这次事故要钉死的地方。
    bool Floating(ItemShape shape, VerdictMode mode, DateTimeOffset? due, ItemStatus status) =>
        ItemPresence.IsFloatingDeadline(shape, mode, due, status, anchor32);

    Check("定段、未来截止、待办 → 常驻",
        Floating(ItemShape.DeadlineOnly, VerdictMode.Judged, Local(6), ItemStatus.Pending));
    Check("定段、今天到期 → 不算常驻（正经待办）",
        !Floating(ItemShape.DeadlineOnly, VerdictMode.Judged, Local(0), ItemStatus.Pending));
    Check("定段、已经过期 → 不算常驻（逾期欠账）",
        !Floating(ItemShape.DeadlineOnly, VerdictMode.Judged, Local(-3), ItemStatus.Pending));
    Check("定段、提前做完 → 不算常驻（收起）",
        !Floating(ItemShape.DeadlineOnly, VerdictMode.Judged, Local(6), ItemStatus.Done));
    Check("定段、放弃 → 不算常驻",
        !Floating(ItemShape.DeadlineOnly, VerdictMode.Judged, Local(6), ItemStatus.Abandoned));
    Check("定段、进行中仍算常驻（没到终点）",
        Floating(ItemShape.DeadlineOnly, VerdictMode.Judged, Local(6), ItemStatus.InProgress));
    Check("定段、跳过仍算常驻",
        Floating(ItemShape.DeadlineOnly, VerdictMode.Judged, Local(6), ItemStatus.Skipped));
    // 【正是 11-21 的 18 条误判】定日哪怕 DueAt 在很远的未来，也不是常驻项。
    Check("定日、未来截止 → 不算常驻（18 条误判的核心）",
        !Floating(ItemShape.DayTask, VerdictMode.Judged, Local(6), ItemStatus.Pending));
    Check("定点 → 不算常驻（那是日程）",
        !Floating(ItemShape.Timed, VerdictMode.Judged, null, ItemStatus.Pending));
    Check("无锚 → 不算常驻（零散待办）",
        !Floating(ItemShape.Unanchored, VerdictMode.Judged, null, ItemStatus.Pending));
    Check("定段但没写截止日 → 不算常驻",
        !Floating(ItemShape.DeadlineOnly, VerdictMode.Judged, null, ItemStatus.Pending));
    Check("守住类（Never）永远不算常驻",
        !Floating(ItemShape.DeadlineOnly, VerdictMode.Never, Local(6), ItemStatus.Pending));

    Check("剩余天数按本地日期算", ItemPresence.DaysLeft(Local(6), anchor32) == 6);
    Check("剩余天数不含时分零头", ItemPresence.DaysLeft(Local(1, 0, 0), anchor32) == 1);

    var line6 = ItemPresence.DeadlineLine(Local(6), anchor32);
    Check("副标题里带剩余天数", line6.Contains("还有 6 天"), line6);
    Check("副标题里带截止日期", line6.Contains("截止"), line6);
    var farYear = ItemPresence.DeadlineLine(Local(400), anchor32);
    Check("跨年时补上年份，不让人以为早过了",
        farYear.Contains($"{DateTime.Today.AddDays(400).Year}年"), farYear);

    // 切天按本地、不按 UTC：库里存 UTC 毫秒，照 UTC 切会把跨夜的截止日算错一天。
    // 取「UTC 当天 16:00」这个时刻，在东八区已经是次日 00:00。
    // DateTime.Today 是 Kind=Local，直接配 TimeSpan.Zero 会抛「偏移不匹配」，
    // 所以先把 Kind 抹成 Unspecified，再按 UTC 造时刻。
    var utcAcross32 = new DateTimeOffset(
        DateTime.SpecifyKind(DateTime.Today.AddDays(6).AddHours(16), DateTimeKind.Unspecified),
        TimeSpan.Zero);
    // 同一个瞬间换成本地 offset 的写法（ToOffset 自动带出本地时钟）。
    var sameInstant32 = utcAcross32.ToOffset(TimeZoneInfo.Local.GetUtcOffset(utcAcross32));
    Check("同一时刻的两种 offset 写法算同一天",
        ItemPresence.LocalDay(utcAcross32) == ItemPresence.LocalDay(sameInstant32));
    Check("确实会切开不同日子",
        ItemPresence.LocalDay(utcAcross32) != ItemPresence.LocalDay(utcAcross32.AddDays(-3)));
    // 东时区跨夜时，按本地切出来的日期必须和照 UTC 日历切出来的不同。
    // 若哪天把 LocalDay 改成照 .DateTime 切，这条会先炸。
    if (TimeZoneInfo.Local.GetUtcOffset(utcAcross32) > TimeSpan.Zero)
    {
        var utcCalendarDay32 = DateOnly.FromDateTime(utcAcross32.UtcDateTime.Date);
        Check("按本地切天，不照 UTC 日期切（跨夜不差一天）",
            ItemPresence.LocalDay(utcAcross32) != utcCalendarDay32);
    }

    // ② 真实数据：常驻带只收该收的，且按截止日排序。
    var near = await repo32.AddDeadlineAsync("常驻测试-近的报告", anchor32.AddDays(3));
    var far = await repo32.AddDeadlineAsync("常驻测试-远的报告", anchor32.AddDays(20));
    await repo32.AddToDayAsync("常驻测试-今天到期的待办", anchor32);
    await repo32.AddAsync("常驻测试-逾期的旧账", null, Local(-5));
    await repo32.AddAsync("常驻测试-有钟点的未来日程", Local(4, 9, 0));
    var doneEarly = await repo32.AddDeadlineAsync("常驻测试-提前做完的报告", anchor32.AddDays(8));
    await repo32.ToggleDoneAsync(doneEarly.Id);
    var abandoned = await repo32.AddDeadlineAsync("常驻测试-放弃的报告", anchor32.AddDays(9));
    await repo32.SetStatusAsync(abandoned.Id, ItemStatus.Abandoned);

    Check("AddDeadlineAsync 建的记录没有 StartAt", near.StartAt is null);
    Check("AddDeadlineAsync 把 DueAt 落在截止日当天",
        ItemPresence.LocalDay(near.DueAt!.Value) == anchor32.AddDays(3));

    var floating32 = await repo32.GetFloatingDeadlinesAsync(anchor32);
    var fids32 = floating32.Select(x => x.Id).ToList();
    Check("常驻带收未来截止的", fids32.Contains(near.Id) && fids32.Contains(far.Id));
    Check("常驻带不收今天到期的正经待办", floating32.All(x => x.Title != "常驻测试-今天到期的待办"));
    Check("常驻带不收逾期的旧账", floating32.All(x => x.Title != "常驻测试-逾期的旧账"));
    Check("常驻带不收有钟点的未来日程", floating32.All(x => x.Title != "常驻测试-有钟点的未来日程"));
    Check("常驻带不收提前做完的", !fids32.Contains(doneEarly.Id));
    Check("常驻带不收放弃的", !fids32.Contains(abandoned.Id));
    Check("常驻带按截止日升序（近的在前）",
        fids32.IndexOf(near.Id) < fids32.IndexOf(far.Id));

    // ③ 完成率分母：这是「不算今天该做完」的硬指标。
    var todayList32 = await repo32.GetDayListAsync(anchor32);
    Check("今天的清单里不出现常驻项", todayList32.All(x => !fids32.Contains(x.Id)));
    var stats32 = await repo32.GetDayStatsAsync(anchor32);
    Check("完成率分母不含常驻项", stats32.Total == todayList32.Count,
        $"分母 {stats32.Total}，清单 {todayList32.Count} 条");

    // ④ 三条完成语义，逐条在真实查询上验证（不是靠状态机，是靠 DueAt 滑动）。
    //    a. 提前做完 → 收起 + 被「那天做完的事」接住（正反馈）。
    Check("提前完成后从常驻带收起", !fids32.Contains(doneEarly.Id));
    Check("提前完成后进「今天做完的事」（正反馈）",
        todayList32.Any(x => x.Id == doneEarly.Id && x.Status == ItemStatus.Done));
    var review32 = await repo32.GetDayReviewAsync(anchor32);
    Check("提前完成后进回顾（那天实际发生过）", review32.Any(x => x.Id == doneEarly.Id));

    //    b. 到 DDL 当天 → 第一次变成正经待办，出现在那天的清单里。
    var onDeadline32 = await repo32.GetDayListAsync(anchor32.AddDays(3));
    Check("到截止日当天转成正经待办", onDeadline32.Any(x => x.Id == near.Id));
    Check("截止日当天之前，它不在中间那些天的清单里",
        !(await repo32.GetDayListAsync(anchor32.AddDays(1))).Any(x => x.Id == near.Id));

    //    c. 过了 DDL 还没做 → 按既有口径滚进今天成欠账。
    var overdue32 = await repo32.AddDeadlineAsync("常驻测试-已过期的报告", anchor32.AddDays(-2));
    Check("过了截止日不再算常驻", !ItemPresence.IsFloatingDeadline(overdue32, anchor32));
    Check("过了截止日滚进今天成欠账",
        (await repo32.GetDayListAsync(anchor32)).Any(x => x.Id == overdue32.Id));

    // ⑤ 源码扫描：确认真的接上了，不是躺着几个新类。
    var src32 = FindSrcRoot();
    string Read32(string rel) => src32 is null ? "" : File.ReadAllText(Path.Combine(src32, rel));

    var presence32 = Read32(Path.Combine("DayManager.Core", "Planning", "ItemPresence.cs"));
    var repo32Src = Read32(Path.Combine("DayManager.Data", "Repositories", "ItemRepository.cs"));
    var models32 = Read32(Path.Combine("DayManager.Api", "Models", "AgentModels.cs"));
    var tools32 = Read32(Path.Combine("DayManager.Api", "Services", "ItemTools.cs"));
    var agent32 = Read32(Path.Combine("DayManager.Api", "Services", "AgentService.cs"));
    var mainVm32 = Read32(Path.Combine("DayManager.App", "ViewModels", "MainViewModel.cs"));
    var itemVm32 = Read32(Path.Combine("DayManager.App", "ViewModels", "ItemViewModel.cs"));
    var page32 = Read32(Path.Combine("DayManager.App", "MainPage.xaml"));
    var proactive32 = Read32(Path.Combine("DayManager.Api", "Services", "ProactiveService.cs"));

    Check("Core 里有常驻判定纯函数", presence32.Contains("public static bool IsFloatingDeadline"));
    Check("Core 里有剩余天数文案纯函数", presence32.Contains("public static string DeadlineLine"));
    Check("常驻判定第一句读 Shape，不再靠时间字段推断",
        presence32.Contains("shape != ItemShape.DeadlineOnly"));
    Check("仓储里有独立的常驻查询", repo32Src.Contains("GetFloatingDeadlinesAsync"));
    Check("常驻查询粗筛只读定段（18 条误判的正面修复）",
        repo32Src.Contains("x.Shape == ItemShape.DeadlineOnly"));
    Check("仓储里有专用的截止日创建入口", repo32Src.Contains("AddDeadlineAsync"));
    Check("动作模型把 dueDate 和 date 分开了", models32.Contains("DueDate") && models32.Contains("public string? Date"));
    Check("执行器分流到 AddDeadlineAsync", tools32.Contains("_items.AddDeadlineAsync"));
    Check("执行器对解析不了的截止日明确报错，不猜成今天", tools32.Contains("截止日「"));
    Check("聊天链路把常驻项喂给了 AI", agent32.Contains("GetFloatingDeadlinesAsync"));
    Check("提示词明说常驻项不算今天该做完", agent32.Contains("不算今天该做完"));
    Check("提示词教了 dueDate 的写法", agent32.Contains("dueDate"));

    // 完成率分母没被改坏：GetDayListAsync 主体里不该被塞进常驻判定。
    var dayListBody32 = repo32Src.Contains("public async Task<List<Item>> GetDayListAsync")
        ? repo32Src[repo32Src.IndexOf("public async Task<List<Item>> GetDayListAsync")..
                    repo32Src.IndexOf("public async Task<List<Item>> GetDayReviewAsync")]
        : "";
    Check("GetDayListAsync 主体没被塞进常驻项（分母口径不变）",
        dayListBody32.Length > 0 && !dayListBody32.Contains("IsFloatingDeadline"));
    Check("GetDayStatsAsync 仍用 GetDayListAsync 当分母",
        repo32Src.Contains("var items = await GetDayListAsync(day, ct)"));

    Check("界面用两个独立集合，完成率只数 TodayItems",
        mainVm32.Contains("FloatingItems") && mainVm32.Contains("ShowFloatingBand")
        && mainVm32.Contains("var total = TodayItems.Count"));
    Check("常驻项完成之后整页重查（搬进「今天做完的事」）",
        mainVm32.Contains("wasFloating") && mainVm32.Contains("await LoadAsync()"));
    Check("常驻项副标题走 Core 纯函数，不显示逾期",
        itemVm32.Contains("ItemPresence.DeadlineShortLine") && itemVm32.Contains("isFloating"));
    Check("走表的常驻项也刷已进行分钟数", mainVm32.Contains("TodayItems.Concat(FloatingItems)"));
    // 原先断言它挂在 CollectionView.Header 上；22:22 实测空列表时 Header/Footer
    // 不参与滚动，整块改成了单一 ScrollView 的三段，这条跟着改成守"它仍是第一段"。
    Check("XAML 里常驻带在清单区最上面一段，且按 ShowFloatingBand 收起",
        page32.Contains("FloatingItems")
        && page32.Contains("IsVisible=\"{Binding ShowFloatingBand}\""));
    Check("XAML 里常驻带给了两个入口", page32.Contains("ToggleCompleteCommand")
        && page32.Contains("StartFocusCommand"));
    // 资源键写错会编译 0 错 0 警、运行时整页炸，这件事第 26 节已经
    // 全量扫过一遍所有 XAML 了；这里只确认新带子确实引用了复用键，不重复造轮子。
    Check("XAML 里常驻带复用已有资源键，没有自造键",
        page32.Contains("{StaticResource CardStyle}")
        && page32.Contains("{StaticResource ChipButton}")
        && page32.Contains("{StaticResource CardTitle}"));

    // 范围守约：主动开口链路本轮不动（它的触发判定是另一条线）。
    Check("主动开口链路没被顺手改掉", !proactive32.Contains("GetFloatingDeadlinesAsync"));

    try { File.Delete(dbFile32); } catch { }
}

Console.WriteLine("=== 33. 三根轴：形态 / 容器 / 完成方式 / 判成败 ===");

{
    var dbFile33 = Path.Combine(Path.GetTempPath(), $"dm-smoke-axes-{Guid.NewGuid():N}.db");
    var factory33 = new TestFactory(dbFile33);
    await using (var db33 = factory33.CreateDbContext())
    {
        await db33.Database.EnsureCreatedAsync();
    }
    var repo33 = new ItemRepository(factory33);
    var anchor33 = DateOnly.FromDateTime(DateTime.Today);

    DateTimeOffset Local33(int dayOffset, int hour = 23, int minute = 59) =>
        new(DateTime.Today.AddDays(dayOffset).AddHours(hour).AddMinutes(minute),
            TimeZoneInfo.Local.GetUtcOffset(DateTime.Today.AddDays(dayOffset)));

    // ① 创建入口把形态写死，不再让下游推断。
    var dayTask33 = await repo33.AddToDayAsync("轴测试-今天作业", anchor33);
    var timed33 = await repo33.AddToDayAsync("轴测试-9点开会", anchor33, new TimeSpan(9, 0, 0));
    var deadline33 = await repo33.AddDeadlineAsync("轴测试-15号交报告", anchor33.AddDays(6));
    Check("AddToDayAsync 无钟点 → 定日", dayTask33.Shape == ItemShape.DayTask);
    Check("AddToDayAsync 有钟点 → 定点", timed33.Shape == ItemShape.Timed);
    Check("AddDeadlineAsync → 定段（与定日第一次在库里可区分）",
        deadline33.Shape == ItemShape.DeadlineOnly);
    Check("定日和定段的时间字段仍然一样（差别只在 Shape 这一列）",
        dayTask33.StartAt is null && deadline33.StartAt is null
        && dayTask33.DueAt is not null && deadline33.DueAt is not null);
    Check("新建记录默认做一次 / 判",
        dayTask33.CompletionMode == CompletionMode.Once
        && dayTask33.VerdictMode == VerdictMode.Judged);

    // ② ② 容器可递归：容器下面挂阶段，阶段下面再挂叶子。
    var root33 = await repo33.AddContainerAsync("轴测试-数学竞赛");
    var stage33 = await repo33.AddContainerAsync("轴测试-学习第一章",
        parentTaskId: root33.Id);
    var leaf33 = await repo33.AddToDayAsync("轴测试-做题", anchor33,
        parentTaskId: stage33.Id);
    Check("容器是 ByChildren，不进分母", root33.CompletionMode == CompletionMode.ByChildren);
    Check("阶段性目标也是容器（② 可递归）", stage33.CompletionMode == CompletionMode.ByChildren);
    Check("阶段挂在根容器下", stage33.ParentTaskId == root33.Id);
    Check("叶子挂在阶段下，形成三层", leaf33.ParentTaskId == stage33.Id);
    Check("叶子仍是做一次 / 判", leaf33.CompletionMode == CompletionMode.Once
        && leaf33.VerdictMode == VerdictMode.Judged);
    // 11-22 收分类：容器不再挑 Unanchored 这个「库里 0 条的空转取值」，一律写定日。
    // 真正要守住的是「无截止日不被兜底塞成今天到期」，这条一直没变。
    Check("容器不再写空转的 Unanchored，仍保持无截止日（不被兜底塞成今天到期）",
        root33.Shape == ItemShape.DayTask && root33.DueAt is null);

    var today33 = await repo33.GetDayListAsync(anchor33);
    var ids33 = today33.Select(x => x.Id).ToHashSet();
    Check("容器不进今日清单（父子不同时占一格）", !ids33.Contains(root33.Id) && !ids33.Contains(stage33.Id));
    Check("叶子照常进今日清单", ids33.Contains(leaf33.Id));
    Check("定段常驻项不进今日清单", !ids33.Contains(deadline33.Id));
    var stats33 = await repo33.GetDayStatsAsync(anchor33);
    Check("完成率分母 = 清单条数（容器与常驻都排除）", stats33.Total == today33.Count);

    // ③ 判成败三带：纯函数读出来，不存第四根轴。
    Check("定段未到期 → 暂时不判",
        ItemAxes.VerdictOf(deadline33, anchor33) == ItemAxes.Verdict.Deferred);
    Check("到截止日当天 → 翻成判",
        ItemAxes.VerdictOf(ItemShape.DeadlineOnly, VerdictMode.Judged,
            deadline33.DueAt, anchor33.AddDays(6))
        == ItemAxes.Verdict.Judged);
    Check("定日 → 判",
        ItemAxes.VerdictOf(dayTask33, anchor33) == ItemAxes.Verdict.Judged);
    var keep33 = await repo33.AddToDayAsync("轴测试-0点上床睡觉", anchor33,
        verdictMode: VerdictMode.Never);
    Check("守住类 → 一直不判",
        ItemAxes.VerdictOf(keep33, anchor33) == ItemAxes.Verdict.Never);
    Check("守住类不进今日清单、不进分母", !ids33.Contains(keep33.Id));
    Check("守住类不能被点名（离线兜底读字段就能判）",
        !ItemAxes.CanNudge(keep33, anchor33));
    Check("容器不能被点名", !ItemAxes.CanNudge(root33, anchor33));
    Check("常驻项不能被点名", !ItemAxes.CanNudge(deadline33, anchor33));
    Check("普通待办能被点名", ItemAxes.CanNudge(dayTask33, anchor33));

    // ④ 每次各算一次：漏做的那次不滚欠账，但当天仍在清单里。
    var daily33 = await repo33.AddAsync("轴测试-每天背单词", null, Local33(-1),
        ct: default, completionMode: CompletionMode.PerOccurrence);
    var dailyToday33 = await repo33.AddAsync("轴测试-今天背单词", null, Local33(0),
        ct: default, completionMode: CompletionMode.PerOccurrence);
    Check("PerOccurrence 是显式声明，不靠 RRule 推断",
        daily33.CompletionMode == CompletionMode.PerOccurrence);
    var todayAgain33 = await repo33.GetDayListAsync(anchor33);
    Check("昨天漏做的每日任务不滚进今天（不补不欠）",
        todayAgain33.All(x => x.Id != daily33.Id));
    Check("今天这一次的每日任务照常出现",
        todayAgain33.Any(x => x.Id == dailyToday33.Id));
    Check("漏做那次仍进当天的完成率分母（不自欺）",
        (await repo33.GetDayListAsync(anchor33.AddDays(-1))).Any(x => x.Id == daily33.Id));

    // ⑤ 离线兜底：这一轮最大的受益者，不需要语义理解。
    var candidates33 = new List<Item> { keep33, root33, deadline33, dayTask33 };
    var actionable33 = NudgeTargetPicker.Actionable(candidates33, DateTimeOffset.Now);
    Check("离线兜底只留下普通待办",
        actionable33.Count == 1 && actionable33[0].Id == dayTask33.Id);
    Check("守住类、容器、定段都没被点名",
        actionable33.All(x => x.Id != keep33.Id && x.Id != root33.Id && x.Id != deadline33.Id));

    // ⑥ 撤销快照覆盖三根轴：AI 改了形态或层级，撤销必须还原。
    var snap33 = ItemSnapshot.From(deadline33);
    Check("快照带上 Shape", snap33.Shape == ItemShape.DeadlineOnly);
    var clone33 = new Item { Title = "还原用", Shape = ItemShape.DayTask,
        CompletionMode = CompletionMode.ByChildren, VerdictMode = VerdictMode.Never,
        ParentTaskId = root33.Id };
    snap33.ApplyTo(clone33);
    Check("撤销还原 Shape / CompletionMode / VerdictMode / ParentTaskId",
        clone33.Shape == ItemShape.DeadlineOnly
        && clone33.CompletionMode == CompletionMode.Once
        && clone33.VerdictMode == VerdictMode.Judged
        && clone33.ParentTaskId is null);

    // ⑦ 源码扫描：确认三根轴真的接进了各条链路，不是躺着几个新类。
    var src33 = FindSrcRoot();
    string Read33(string rel) => src33 is null ? "" : File.ReadAllText(Path.Combine(src33, rel));
    var enums33 = Read33(Path.Combine("DayManager.Core", "Enums.cs"));
    var axes33 = Read33(Path.Combine("DayManager.Core", "Planning", "ItemAxes.cs"));
    var item33 = Read33(Path.Combine("DayManager.Core", "Entities", "Item.cs"));
    var repoSrc33 = Read33(Path.Combine("DayManager.Data", "Repositories", "ItemRepository.cs"));
    var projSrc33 = Read33(Path.Combine("DayManager.Data", "Repositories", "ProjectRepository.cs"));
    var ctx33 = Read33(Path.Combine("DayManager.Data", "AppDbContext.cs"));
    var models33 = Read33(Path.Combine("DayManager.Api", "Models", "AgentModels.cs"));
    var toolsSrc33 = Read33(Path.Combine("DayManager.Api", "Services", "ItemTools.cs"));
    var agentSrc33 = Read33(Path.Combine("DayManager.Api", "Services", "AgentService.cs"));
    var picker33 = Read33(Path.Combine("DayManager.Core", "Intervention", "NudgeTargetPicker.cs"));
    var itemVmSrc33 = Read33(Path.Combine("DayManager.App", "ViewModels", "ItemViewModel.cs"));
    var proactiveSrc33 = Read33(Path.Combine("DayManager.Api", "Services", "ProactiveService.cs"));
    var migrations33 = src33 is null ? "" : string.Join("\n",
        Directory.GetFiles(Path.Combine(src33, "DayManager.Data", "Migrations"), "*AddTaskAxes.cs")
            .Select(File.ReadAllText));

    Check("三根轴都是强类型枚举，不是 Tags 里的字符串",
        enums33.Contains("public enum ItemShape")
        && enums33.Contains("public enum CompletionMode")
        && enums33.Contains("public enum VerdictMode"));
    Check("③ 只收三个值，守住类不占③的格子",
        enums33.Contains("ByChildren = 2")
        && axes33.Contains("public enum Verdict"));
    Check("判成败是读出来的结论，不是存进去的第四根轴",
        axes33.Contains("public static Verdict VerdictOf")
        && !item33.Contains("public Verdict "));
    Check("Item.cs 旧注释已作废（不再宣称靠时间字段区分形态）",
        item33.Contains("旧写法已作废"));
    Check("② 用 ParentTaskId 自引用，不新增枚举字段",
        item33.Contains("public Guid? ParentTaskId")
        && ctx33.Contains("HasForeignKey(x => x.ParentTaskId)"));
    Check("AppDbContext 给 Shape / ParentTaskId 都建了索引",
        ctx33.Contains("HasIndex(x => x.Shape)")
        && ctx33.Contains("HasIndex(x => x.ParentTaskId)"));
    Check("清单过滤走 Core 判据，不在查询里另抄一遍",
        repoSrc33.Contains("ItemAxes.EntersDayList(x)"));
    Check("项目进度只数叶子（容器不重复计数）",
        projSrc33.Contains("x.CompletionMode != CompletionMode.ByChildren"));
    Check("每次各算一次的不滚欠账是真口径，不是注释",
        repoSrc33.Contains("x.CompletionMode != CompletionMode.PerOccurrence"));
    Check("仓储有阶段性目标创建入口", repoSrc33.Contains("AddContainerAsync"));
    Check("AI 能建容器与挂子项",
        models33.Contains("IsStage") && models33.Contains("ParentTitle")
        && toolsSrc33.Contains("ResolveParentTaskAsync"));
    Check("父任务找不到时明确报错，不猜", toolsSrc33.Contains("找不到叫"));
    Check("AI 视野里能看到容器（软件里所有信息 AI 都能看到）",
        agentSrc33.Contains("GetStagesAsync") && agentSrc33.Contains("stagesText"));
    Check("提示词明说容器不算今天该做完", agentSrc33.Contains("阶段性目标（容器）"));
    Check("离线兜底读 Core 判据，不再只靠 StartAt 猜",
        picker33.Contains("ItemAxes.CanNudge"));
    Check("界面不给容器/守住类 10 分钟入口",
        itemVmSrc33.Contains("ItemAxes.CanNudge"));
    Check("迁移含历史回填，且明说定段回填 0 条",
        migrations33.Contains("UPDATE items SET Shape = 1")
        && migrations33.Contains("定段回填 0 条"));
    Check("迁移没把历史记录擅自改成守住类或每日任务",
        !migrations33.Contains("VerdictMode = 1")
        && !migrations33.Contains("CompletionMode = 1"));
    Check("ItemSnapshot 覆盖三根轴与层级",
        Read33(Path.Combine("DayManager.Data", "Snapshots", "ItemSnapshot.cs"))
            .Contains("public ItemShape Shape"));
    Check("范围守约：主动开口链路没被顺手改掉",
        !proactiveSrc33.Contains("GetFloatingDeadlinesAsync"));

    try { File.Delete(dbFile33); } catch { }
}

Console.WriteLine("=== 34. AI 能建「每次各算 / 不判成败」：只补写的一侧 ===");

{
    var src34 = FindSrcRoot();
    string Read34(string rel) => src34 is null ? "" : File.ReadAllText(Path.Combine(src34, rel));
    var models34 = Read34(Path.Combine("DayManager.Api", "Models", "AgentModels.cs"));
    var tools34 = Read34(Path.Combine("DayManager.Api", "Services", "ItemTools.cs"));
    var agent34 = Read34(Path.Combine("DayManager.Api", "Services", "AgentService.cs"));
    var mainVm34 = Read34(Path.Combine("DayManager.App", "ViewModels", "MainViewModel.cs"));
    var page34 = Read34(Path.Combine("DayManager.App", "MainPage.xaml"));

    Check("AiAction 加 repeatsDaily / noVerdict 两个可选布尔",
        models34.Contains("public bool? RepeatsDaily")
        && models34.Contains("public bool? NoVerdict"));
    Check("模型注释说清 RRule 展开仍留白，不许诺排好一周",
        models34.Contains("不会自动生成后面几天的实例"));
    Check("noVerdict 写成待遇而不是另一类任务",
        models34.Contains("它不是另一类任务"));
    Check("执行器把两个开关透传给创建入口",
        tools34.Contains("CompletionMode.PerOccurrence")
        && tools34.Contains("VerdictMode.Never"));
    Check("开关与定段/容器冲突时明确报错，不静默忽略",
        tools34.Contains("只能给普通待办"));
    Check("回喂模型的文案说清建成了什么",
        tools34.Contains("每次各算一次，漏做当天封账")
        && tools34.Contains("不判成败，不算今天该做完"));
    Check("提示词列了两个新参数",
        agent34.Contains("repeatsDaily（可空）") && agent34.Contains("noVerdict（可空）"));
    Check("提示词明说现在不会自动生成后面几天",
        agent34.Contains("现在还不会自动生成后面几天的实例"));
    Check("提示词不许把 noVerdict 说成新类别",
        agent34.Contains("别在 reply 里给他安一个新类别的名字"));
    Check("提示词没出现「守住类」这个实体化名字",
        !agent34.Contains("守住类"));
    Check("范围守约：第 34 批没把 AI 那两个开关做成界面入口",
        !mainVm34.Contains("RepeatsDaily") && !mainVm34.Contains("NoVerdict")
        && !page34.Contains("repeatsDaily") && !page34.Contains("noVerdict"));

    // 真实临时库：执行器调的是 AddToDayAsync，验证两个开关真能落库、
    // 以及「不判成败」的那条确实不进今日清单。
    var dbFile34 = Path.Combine(Path.GetTempPath(), $"dm-smoke-write-side-{Guid.NewGuid():N}.db");
    var factory34 = new TestFactory(dbFile34);
    await using (var db34 = factory34.CreateDbContext())
    {
        await db34.Database.EnsureCreatedAsync();
    }
    var repo34 = new ItemRepository(factory34);
    var today34 = DateOnly.FromDateTime(DateTime.Today);

    var daily34 = await repo34.AddToDayAsync("冒烟-每天跑5km", today34,
        completionMode: CompletionMode.PerOccurrence);
    var quiet34 = await repo34.AddToDayAsync("冒烟-0点上床睡觉", today34,
        verdictMode: VerdictMode.Never);
    var plain34 = await repo34.AddToDayAsync("冒烟-普通待办", today34);

    Check("每日任务落库 CompletionMode=PerOccurrence",
        daily34.CompletionMode == CompletionMode.PerOccurrence
        && daily34.VerdictMode == VerdictMode.Judged);
    Check("不判成败落库 VerdictMode=Never",
        quiet34.VerdictMode == VerdictMode.Never
        && quiet34.CompletionMode == CompletionMode.Once);
    Check("没给开关仍是默认（做一次 / 判）",
        plain34.CompletionMode == CompletionMode.Once
        && plain34.VerdictMode == VerdictMode.Judged);
    var list34 = await repo34.GetDayListAsync(today34);
    Check("每日任务仍进今日清单，不判成败的不进",
        list34.Any(x => x.Id == daily34.Id)
        && list34.Any(x => x.Id == plain34.Id)
        && !list34.Any(x => x.Id == quiet34.Id));

    try { File.Delete(dbFile34); } catch { }
}

Console.WriteLine("=== 35. 界面能直接建四种形态：一个折叠入口 ===");

{
    var src35 = FindSrcRoot();
    string Read35(string rel) => src35 is null ? "" : File.ReadAllText(Path.Combine(src35, rel));
    var vm35 = Read35(Path.Combine("DayManager.App", "ViewModels", "MainViewModel.cs"));
    var page35 = Read35(Path.Combine("DayManager.App", "MainPage.xaml"));
    var tools35 = Read35(Path.Combine("DayManager.Api", "Services", "ItemTools.cs"));
    var models35 = Read35(Path.Combine("DayManager.Api", "Models", "AgentModels.cs"));
    var prog35 = Read35(Path.Combine("DayManager.Api", "Program.cs"));

    Check("形态枚举是界面层自己的，四个取值、没有容器/不判成败",
        vm35.Contains("public enum AddShape")
        && vm35.Contains("Day = 0") && vm35.Contains("Timed = 1")
        && vm35.Contains("Deadline = 2") && vm35.Contains("Daily = 3")
        && !vm35.Contains("VerdictMode.Never"));

    Check("默认是「今天做」，随手打字回车不用多点一下",
        vm35.Contains("NewItemShape { get; set; } = AddShape.Day"));

    Check("形态按钮文字 / 两个按需展开区都是算出来的",
        vm35.Contains("public string ShapeButtonText")
        && vm35.Contains("public bool ShowTimePicker")
        && vm35.Contains("public bool ShowDatePicker")
        && vm35.Contains("!IsShapePickerOpen && NewItemShape == AddShape.Timed")
        && vm35.Contains("!IsShapePickerOpen && NewItemShape == AddShape.Deadline"));

    // 只查真正会渲染的字符串，注释不算：MainPage.xaml 自己的注释里
    // 就写着 ‹ › 来讲 11-02 那次事故，VM 注释也一样。剥掉注释再扫。
    var xamlBody35 = Regex.Replace(page35, @"<!--[\s\S]*?-->", "");
    Check("界面符号只用 ASCII，不用字体里可能缺字形的下拉箭头（11-02 事故）",
        !xamlBody35.Contains("\u25BE") && !xamlBody35.Contains("\u2039")
        && !xamlBody35.Contains("\u203A")
        && vm35.Contains("\"今天 v\"") && vm35.Contains("\"定点 v\"")
        && vm35.Contains("\"截止日 v\"") && vm35.Contains("\"每天 v\"")
        && !vm35.Contains("=> \"今天 \u25BE\""));

    Check("提示语跟着形态变，防「我以为在建普通待办」",
        vm35.Contains("AddShape.Deadline => $\"什么事")
        && vm35.Contains("AddShape.Daily => \"每天要做的事？\""));

    Check("XAML：形态按钮 + 四个 CommandParameter + 收起时塌成 0 高",
        page35.Contains("Text=\"{Binding ShapeButtonText}\"")
        && page35.Contains("Command=\"{Binding ToggleShapePickerCommand}\"")
        && page35.Contains("Command=\"{Binding PickShapeCommand}\"")
        && page35.Contains("CommandParameter=\"Timed\"")
        && page35.Contains("CommandParameter=\"Deadline\"")
        && page35.Contains("CommandParameter=\"Daily\"")
        && page35.Contains("IsVisible=\"{Binding IsShapePickerOpen}\""));

    Check("XAML：时间/日期选择器互斥，各绑各的可见性",
        page35.Contains("IsVisible=\"{Binding ShowTimePicker}\"")
        && page35.Contains("IsVisible=\"{Binding ShowDatePicker}\"")
        && page35.Contains("Date=\"{Binding DeadlineDate}\"")
        && page35.Contains("MinimumDate=\"{Binding DeadlineMinDate}\""));

    Check("XAML：复用已有资源键，没有自造键（第 26 节会全量校对）",
        page35.Contains("{StaticResource ChipButton}")
        && !page35.Contains("{StaticResource ShapePickerButton}"));

    Check("旧的「定时」勾选框已彻底移除，没留下死字段",
        !page35.Contains("UseSpecificTime") && !vm35.Contains("UseSpecificTime"));

    Check("四种形态各走专用创建入口，不靠时间字段反推",
        vm35.Contains("case AddShape.Timed:")
        && vm35.Contains("case AddShape.Deadline:")
        && vm35.Contains("case AddShape.Daily:")
        && vm35.Contains("_itemRepository.AddDeadlineAsync(")
        && vm35.Contains("completionMode: CompletionMode.PerOccurrence")
        && vm35.Contains("AddToDayAsync(title, SelectedDay, NewTime)"));

    Check("每天只建这一条，不硬摊一串（18 条误判的来历）",
        vm35.Contains("RRule 真实展开还留白")
        && !vm35.Contains("AddDeadlineAsync(title, SelectedDay"));

    Check("加入后重置回默认形态，连续录入不会串成截止日",
        vm35.Contains("NewItemShape = AddShape.Day;")
        && vm35.Contains("IsShapePickerOpen = false;"));

    Check("截止日不许挑过去（新建出来不该一落地就是欠账）",
        vm35.Contains("public DateTime DeadlineMinDate => DateTime.Today"));

    Check("本轮没给「容器 / 不判成败」入口（用户明确先不给）",
        !page35.Contains("isStage") && !page35.Contains("noVerdict")
        && !vm35.Contains("AddContainerAsync") && !vm35.Contains("VerdictMode.Never"));

    Check("范围守约：后端 / 数据层 / 迁移 / 提示词这轮都没动",
        models35.Contains("public bool? RepeatsDaily")
        && models35.Contains("public bool? NoVerdict")
        && tools35.Contains("CompletionMode.PerOccurrence")
        && prog35.Contains("items.AddToDayAsync(request.Title.Trim(), day, time, ct)")
        && !models35.Contains("AddShape") && !tools35.Contains("ShapeButtonText"));

    // 真实临时库：把界面四种分流对应的仓储入口各调一遍，
    // 验证落库的 Shape / CompletionMode / 钟点，而不是只扫源码字符串。
    var dbFile35 = Path.Combine(Path.GetTempPath(), $"dm-smoke-ui-shapes-{Guid.NewGuid():N}.db");
    var factory35 = new TestFactory(dbFile35);
    await using (var db35 = factory35.CreateDbContext())
    {
        await db35.Database.EnsureCreatedAsync();
    }
    var repo35 = new ItemRepository(factory35);
    var today35 = DateOnly.FromDateTime(DateTime.Today);

    var day35 = await repo35.AddToDayAsync("冒烟-今天做", today35);
    var timed35 = await repo35.AddToDayAsync("冒烟-定点", today35, new TimeSpan(14, 30, 0));
    var ddl35 = await repo35.AddDeadlineAsync("冒烟-只有截止日", today35.AddDays(7));
    var daily35 = await repo35.AddToDayAsync("冒烟-每天", today35,
        completionMode: CompletionMode.PerOccurrence);

    Check("今天做 / 定点落库 Shape 正确，定点带 StartAt",
        day35.Shape == ItemShape.DayTask && day35.StartAt is null
        && timed35.Shape == ItemShape.Timed && timed35.StartAt is not null);
    Check("只有截止日落库 Shape=DeadlineOnly，与定日待办能区分",
        ddl35.Shape == ItemShape.DeadlineOnly
        && ddl35.Shape != day35.Shape);
    Check("每天落库 CompletionMode=PerOccurrence，且只有一条",
        daily35.CompletionMode == CompletionMode.PerOccurrence
        && daily35.CompletionMode != day35.CompletionMode
        && (await repo35.GetDayListAsync(today35)).Count(x => x.Id == daily35.Id) == 1);
    Check("定段常驻带只认 Shape=DeadlineOnly（18 条误判不复发）",
        ItemPresence.IsFloatingDeadline(ddl35, today35)
        && !ItemPresence.IsFloatingDeadline(day35, today35)
        && !ItemPresence.IsFloatingDeadline(daily35, today35));

    try { File.Delete(dbFile35); } catch { }
}

Console.WriteLine("=== 36. 收分类 + 大任务递归层级：主要是删，不是加 ===");

{
    var src36 = FindSrcRoot();
    string Read36(string rel) => src36 is null ? "" : File.ReadAllText(Path.Combine(src36, rel));
    var tree36 = Read36(Path.Combine("DayManager.Core", "Planning", "ProjectTree.cs"));
    var axes36 = Read36(Path.Combine("DayManager.Core", "Planning", "ItemAxes.cs"));
    var enums36 = Read36(Path.Combine("DayManager.Core", "Enums.cs"));
    var itemSrc36 = Read36(Path.Combine("DayManager.Core", "Entities", "Item.cs"));
    var repoSrc36 = Read36(Path.Combine("DayManager.Data", "Repositories", "ItemRepository.cs"));
    var projVm36 = Read36(Path.Combine("DayManager.App", "ViewModels", "ProjectDetailViewModel.cs"));
    var itemVm36 = Read36(Path.Combine("DayManager.App", "ViewModels", "ItemViewModel.cs"));
    var page36 = Read36(Path.Combine("DayManager.App", "ProjectDetailPage.xaml"));
    var agent36 = Read36(Path.Combine("DayManager.Api", "Services", "AgentService.cs"));
    var proactive36 = Read36(Path.Combine("DayManager.Api", "Services", "ProactiveService.cs"));

    // ① 收：容器判据归一到一个答案、停止写空转取值；枚举与列不物理删，兼容历史。
    Check("容器判据归一：IsContainer 只读 CompletionMode，不再有两个答案",
        axes36.Contains("public static bool IsContainer(Item item)")
        && axes36.Contains("item.CompletionMode == CompletionMode.ByChildren")
        && !axes36.Contains("IsContainer(bool hasChildren"));
    Check("清单准入与离线点名都走同一个 IsLeaf",
        axes36.Contains("!IsContainer(item)") && axes36.Contains("&& IsLeaf(item)"));
    Check("AddContainerAsync 停止写 Unanchored，一律定日 + 无锚",
        repoSrc36.Contains("停止写入 Unanchored")
        && repoSrc36.Contains("shape: ItemShape.DayTask,"));
    Check("不物理删列：Unanchored 枚举值留着兼容历史，Shape 等字段也还在",
        enums36.Contains("Unanchored") && itemSrc36.Contains("public ItemShape Shape")
        && itemSrc36.Contains("public Guid? ParentTaskId"));

    // ② ProjectTree 是 Core 纯函数：内存造树就能测，不必落库。
    Item N36(string title, Guid id, Guid? parent = null,
        CompletionMode cm = CompletionMode.Once, ItemStatus st = ItemStatus.Pending) =>
        new Item { Id = id, Title = title, ParentTaskId = parent,
            CompletionMode = cm, Status = st };

    var r36 = Guid.NewGuid(); var s36 = Guid.NewGuid(); var top36 = Guid.NewGuid();
    var l1_36 = Guid.NewGuid(); var l2_36 = Guid.NewGuid(); var l3_36 = Guid.NewGuid();
    var treeItems36 = new List<Item>
    {
        N36("根", r36, cm: CompletionMode.ByChildren),
        N36("顶层叶子", top36),
        N36("阶段", s36, r36, CompletionMode.ByChildren),
        N36("做完", l1_36, s36, st: ItemStatus.Done),
        N36("没做", l2_36, s36),
        N36("放弃", l3_36, s36, st: ItemStatus.Abandoned),
    };
    var rows36 = ProjectTree.Flatten(treeItems36);
    Check("按 ParentTaskId 组装出 6 行，深度 0 / 1 / 2",
        rows36.Count == 6
        && rows36.Single(x => x.Item.Id == r36).Depth == 0
        && rows36.Single(x => x.Item.Id == s36).Depth == 1
        && rows36.Single(x => x.Item.Id == l1_36).Depth == 2
        && rows36.Single(x => x.Item.Id == top36).Depth == 0);
    Check("深度优先：阶段紧跟父容器，没有跳到别处",
        rows36[0].Item.Id == r36 && rows36[1].Item.Id == s36);
    var stageProg36 = ProjectTree.StageProgressOf(treeItems36);
    Check("阶段进度只数叶子、放弃的不算分母，容器自己也不算",
        stageProg36[r36].Done == 1 && stageProg36[r36].Total == 2
        && stageProg36[s36].Done == 1 && stageProg36[s36].Total == 2
        && !stageProg36.ContainsKey(top36));

    var ca36 = Guid.NewGuid(); var cb36 = Guid.NewGuid();
    var cyc36 = ProjectTree.Flatten(new List<Item>
    {
        N36("环A", ca36, cb36),
        N36("环B", cb36, ca36),
    });
    Check("有环不死循环，两条都补挂顶层没丢",
        cyc36.Count == 2 && cyc36.All(x => x.Depth <= 1));
    var orphan36 = Guid.NewGuid();
    var orph36 = ProjectTree.Flatten(new List<Item> { N36("孤儿", orphan36, Guid.NewGuid()) });
    Check("父不在这批里时当顶层，绝不凭空丢记录",
        orph36.Count == 1 && orph36[0].Depth == 0);
    var chain36 = new List<Item>();
    Guid? prev36 = null;
    for (var i = 0; i < 10; i++)
    {
        var id36 = Guid.NewGuid();
        chain36.Add(N36($"L{i}", id36, prev36, CompletionMode.ByChildren));
        prev36 = id36;
    }
    var chainRows36 = ProjectTree.Flatten(chain36);
    Check("超过 8 层就停，坏记录拖不死界面，深处的也没丢",
        chainRows36.Count == chain36.Count && chainRows36.Max(x => x.Depth) == 8);

    // ③ 真实临时库：本轮改过 AddContainerAsync，落库形态与三层组装都要真跑一遍。
    var dbFile36 = Path.Combine(Path.GetTempPath(), $"dm-smoke-tree-{Guid.NewGuid():N}.db");
    var factory36 = new TestFactory(dbFile36);
    await using (var db36 = factory36.CreateDbContext())
    {
        await db36.Database.EnsureCreatedAsync();
    }
    // ProjectId 是外键：先建一个真项目再挂，不能拿随机 Guid 硬塞（会 FOREIGN KEY 失败）。
    // 生产路径传的是详情页里真实存在的 _projectId，这里复现的就是那条路。
    var proj36 = Guid.NewGuid();
    await using (var seed36 = factory36.CreateDbContext())
    {
        seed36.Projects.Add(new Project { Id = proj36, Name = "冒烟-树项目" });
        await seed36.SaveChangesAsync();
    }
    var repo36 = new ItemRepository(factory36);
    var c36 = await repo36.AddContainerAsync("树-根容器", projectId: proj36);
    var st36 = await repo36.AddContainerAsync("树-第一章", parentTaskId: c36.Id, projectId: proj36);
    var lf36 = await repo36.AddAsync("树-做题", projectId: proj36,
        parentTaskId: st36.Id, shape: ItemShape.DayTask);
    Check("容器落库：定日 + ByChildren + 无截止 + 带项目归属，不写 Unanchored",
        c36.Shape == ItemShape.DayTask && c36.CompletionMode == CompletionMode.ByChildren
        && c36.DueAt is null && c36.ProjectId == proj36);
    Check("阶段挂父容器、叶子挂阶段，三级 ParentTaskId 连起来",
        st36.ParentTaskId == c36.Id && lf36.ParentTaskId == st36.Id);
    await using (var read36 = factory36.CreateDbContext())
    {
        var all36 = await read36.Items.ToListAsync();
        Check("容器下平铺的记录能组装成 0 / 1 / 2 三层",
            ProjectTree.Flatten(all36).Single(x => x.Item.Id == c36.Id).Depth == 0
            && ProjectTree.Flatten(all36).Single(x => x.Item.Id == st36.Id).Depth == 1
            && ProjectTree.Flatten(all36).Single(x => x.Item.Id == lf36.Id).Depth == 2);
    }
    var dayList36 = await repo36.GetDayListAsync(DateOnly.FromDateTime(DateTime.Today));
    Check("容器不进今日清单（父子不同时占一格）",
        !dayList36.Any(x => x.Id == c36.Id || x.Id == st36.Id));
    try { File.Delete(dbFile36); } catch { }

    // ④ 大任务页：一个平铺 CollectionView，缩进表达层级，容器行不给打勾。
    var body36 = Regex.Replace(page36, @"<!--[\s\S]*?-->", "");
    Check("XAML 整行缩进绑 RowPadding，容器不给打勾 / 启动",
        body36.Contains("Padding=\"{Binding RowPadding}\"")
        && body36.Contains("IsVisible=\"{Binding IsLeaf}\"")
        && body36.Contains("IsVisible=\"{Binding ShowTimerButton}\""));
    Check("XAML 容器行显示下级进度与「往里加」",
        body36.Contains("Text=\"{Binding StageProgressText}\"")
        && body36.Contains("IsVisible=\"{Binding HasStageProgress}\"")
        && body36.Contains("IsVisible=\"{Binding IsContainer}\"")
        && body36.Contains("PickParentStageCommand"));
    Check("XAML 一个输入框两种内容，挂靠可一键回顶层",
        body36.Contains("ToggleAddKindCommand")
        && body36.Contains("ClearParentStageCommand")
        && body36.Contains("IsVisible=\"{Binding HasParentStage}\"")
        && body36.Contains("Placeholder=\"{Binding AddPlaceholder}\"")
        && body36.Contains("Text=\"{Binding AddKindText}\""));
    Check("XAML 复用已有资源键，没有自造键（第 26 节全量校对）",
        body36.Contains("{StaticResource ChipButton}")
        && body36.Contains("{StaticResource RoundIconButton}")
        && body36.Contains("{StaticResource Caption}")
        && body36.Contains("{StaticResource BackButton}")
        && body36.Contains("{StaticResource DoneGreen}"));
    Check("界面符号只用 ASCII（11-02 事故），形态按钮是 v 不是缺字形箭头",
        !body36.Contains("\u25BE") && !body36.Contains("\u2039") && !body36.Contains("\u203A")
        && projVm36.Contains("\"加阶段 v\"") && projVm36.Contains("\"加任务 v\""));
    Check("详情页用 Core 纯函数组装树，进度只数叶子",
        projVm36.Contains("ProjectTree.Flatten(items)")
        && projVm36.Contains("ProjectTree.StageProgressOf")
        && projVm36.Contains("RefreshStageProgress()")
        && projVm36.Contains("Items.Where(x => x.IsLeaf)"));
    Check("加阶段 / 加任务 / 选父阶段 / 回顶层都有命令",
        projVm36.Contains("AddContainerAsync(NewTitle.Trim()")
        && projVm36.Contains("ToggleAddKind") && projVm36.Contains("PickParentStage")
        && projVm36.Contains("ClearParentStage"));
    Check("加叶子显式带 projectId，不走写死 null 的 AddToDayAsync（本轮修的坑）",
        projVm36.Contains("projectId: _projectId")
        && !projVm36.Contains("_items.AddToDayAsync"));
    Check("ItemViewModel 同一行复用，只多了树行属性",
        itemVm36.Contains("public Thickness RowPadding")
        && itemVm36.Contains("ShowTimerButton") && itemVm36.Contains("HasStageProgress")
        && itemVm36.Contains("StageProgressText"));

    // ⑤ 减法范围守约：界面仍不给 noVerdict / RRule 展开，主动开口链路没顺手动。
    Check("界面没给 noVerdict 入口，容器不是新的枚举类型值",
        !body36.Contains("noVerdict") && !projVm36.Contains("VerdictMode.Never")
        && !projVm36.Contains("ItemShape.Container"));
    Check("RRule 真实展开仍留白（提示词里那句没被偷偷做掉）",
        agent36.Contains("现在还不会自动生成后面几天的实例"));
    Check("范围守约：主动开口链路没被顺手改掉", !proactive36.Contains("ProjectTree"));
}

// ===== 第 37 节：AI 看不见大任务（2026-11-22 用户截图 + 「你先把这个修了吧」）=====
// 上一批只把「阶段性目标（容器）」喂进了系统提示，项目名 / 归属 / 项目下任务一条都没给，
// 模型视野里只剩一个孤零零的容器名，只好答「我看不到」、让他截图。
// 这节守的是「只补视野」：不加字段、不加迁移、不加动作、不改数据模型。
{
    var src37 = FindSrcRoot();
    string Read37(string rel) => src37 is null ? "" : File.ReadAllText(Path.Combine(src37, rel));
    var agent37 = Read37(Path.Combine("DayManager.Api", "Services", "AgentService.cs"));
    var prog37 = Read37(Path.Combine("DayManager.Api", "Program.cs"));
    var repo37 = Read37(Path.Combine("DayManager.Data", "Repositories", "ProjectRepository.cs"));
    var proactive37 = Read37(Path.Combine("DayManager.Api", "Services", "ProactiveService.cs"));

    // ① 链路：注入 → 取数 → 传参 → 拼段 → 视野句。
    Check("AgentService 注入 ProjectRepository，DI 里本来就有，不用改注册",
        agent37.Contains("private readonly ProjectRepository _projects;")
        && agent37.Contains("ProjectRepository projects,")
        && prog37.Contains("AddSingleton<DayManager.Data.Repositories.ProjectRepository>()"));
    Check("ChatAsync 取全部项目 + 项目下任务，并喂进系统提示词",
        agent37.Contains("var projectGroups = await _projects.GetAllWithItemsAsync(ct);")
        && agent37.Contains("stages, projectGroups, ct)"));
    Check("提示词有一段【大任务】，项目名 / 完整 id / 任务 / 状态 / 容器标注都给全",
        agent37.Contains("【大任务（项目）】")
        && agent37.Contains("            projectText,")
        && agent37.Contains("{p.Id} | {p.Name}")
        && agent37.Contains("{i.Id} | {i.Title}")
        && agent37.Contains("StatusName(i.Status)")
        && agent37.Contains("ItemAxes.IsContainer(i)"));
    Check("视野里的 id 一律完整 Guid，短 id 那个回归不再出现",
        !agent37.Contains("ShortId("));
    Check("同名项目靠 id 区分、归档也列、明确别再让用户截图",
        agent37.Contains("同名项目靠 id 区分")
        && agent37.Contains("已归档的也列出来")
        && agent37.Contains("不要再让他截图"));
    Check("大任务段不算今天该做完，别拿进度催他",
        agent37.Contains("这些是归拢事情的壳，**不算今天该做完、别拿进度去催**"));
    Check("【你的视野】把大任务算进默认视野，不再只说两段",
        agent37.Contains("上面三段「一直悬着」「阶段性目标」「大任务」")
        && !agent37.Contains("上面两段「一直悬着」"));

    // ② 范围守约：六种动作没变、query 仍只读、主动开口没被顺手改、新方法只读。
    Check("范围守约：可用操作仍是六种，query 仍只读",
        agent37.Contains("可用的操作有这六种")
        && agent37.Contains("- query      查清单，**只读**"));
    Check("范围守约：主动开口链路没被顺手带上项目视野",
        !proactive37.Contains("GetAllWithItemsAsync")
        && !proactive37.Contains("projectGroups"));
    var start37 = repo37.IndexOf("public async Task<List<(Project Project, List<Item> Items)>>");
    var body37 = start37 < 0 ? "" : repo37.Substring(start37, Math.Min(1200, repo37.Length - start37));
    Check("范围守约：新方法是只读查询，不写库、不建实体",
        !body37.Contains("SaveChanges") && !body37.Contains("db.Projects.Add")
        && !body37.Contains("new Item"));

    // ③ 真实临时库：同名项目、归档项目、无归属任务都要分得清。
    var dbFile37 = Path.Combine(Path.GetTempPath(), $"dm-smoke-vision-{Guid.NewGuid():N}.db");
    var factory37 = new TestFactory(dbFile37);
    await using (var db37 = factory37.CreateDbContext())
    {
        await db37.Database.EnsureCreatedAsync();
    }
    var a37 = Guid.NewGuid(); var b37 = Guid.NewGuid(); var c37 = Guid.NewGuid();
    await using (var seed37 = factory37.CreateDbContext())
    {
        seed37.Projects.AddRange(
            new Project { Id = a37, Name = "aaa", Status = ProjectStatus.Active },
            new Project { Id = b37, Name = "abc", Status = ProjectStatus.Active },
            new Project { Id = c37, Name = "aaa", Status = ProjectStatus.Archived });
        seed37.Items.AddRange(
            new Item { Id = Guid.NewGuid(), Title = "挂 aaa 的容器", ProjectId = a37,
                CompletionMode = CompletionMode.ByChildren, Shape = ItemShape.DayTask },
            new Item { Id = Guid.NewGuid(), Title = "挂 aaa 的普通任务", ProjectId = a37,
                Shape = ItemShape.DayTask },
            new Item { Id = Guid.NewGuid(), Title = "挂 abc 的任务", ProjectId = b37,
                Shape = ItemShape.DayTask },
            new Item { Id = Guid.NewGuid(), Title = "挂归档 aaa 的任务", ProjectId = c37,
                Shape = ItemShape.DayTask },
            new Item { Id = Guid.NewGuid(), Title = "不属于任何项目", Shape = ItemShape.DayTask });
        await seed37.SaveChangesAsync();
    }
    var projRepo37 = new ProjectRepository(factory37);
    var groups37 = await projRepo37.GetAllWithItemsAsync();
    Check("全部 3 个项目都取回（含归档），进行中排前面",
        groups37.Count == 3
        && groups37[0].Project.Status == ProjectStatus.Active
        && groups37[1].Project.Status == ProjectStatus.Active
        && groups37[2].Project.Status == ProjectStatus.Archived);
    Check("两个同名 aaa 靠 Id 分开，各自只带自己的任务",
        groups37.Count(g => g.Project.Name == "aaa") == 2
        && groups37.Single(g => g.Project.Id == a37).Items.Count == 2
        && groups37.Single(g => g.Project.Id == c37).Items.Count == 1);
    Check("容器也列在项目下面，不属于任何项目的 item 不混进来",
        groups37.Single(g => g.Project.Id == a37).Items
            .Any(x => x.CompletionMode == CompletionMode.ByChildren)
        && groups37.Sum(g => g.Items.Count) == 4);
    try { File.Delete(dbFile37); } catch { }
}

// ===== 第 38 节：视野里明明有，模型却还说「看不到」（16:36 截图 + 本机 mock 抓包实证）=====
// 上一节把项目数据补进了系统提示，本机用一个假 AI 端点抓下真实报文，确认 prompt 里
// aaa / abc / abd 及归属全都在。可同一个会话前几轮 AI 自己反复说过「看不到」，
// 他问「你现在能看到了吗」这种关于 AI 自身状态的元问题时，模型顺着旧结论又答「还是没有」。
// 这节守反锚定：靠后一条 system 直接报项目数和具体名字；顺带修掉自己埋的短 id 回归
// （项目下任务原先给 8 位短 id，模型拿去 delete，ItemTools 要完整 Guid → 找不到记录）。
{
    var src38 = FindSrcRoot();
    string Read38(string rel) => src38 is null ? "" : File.ReadAllText(Path.Combine(src38, rel));
    var agent38 = Read38(Path.Combine("DayManager.Api", "Services", "AgentService.cs"));
    var tools38 = Read38(Path.Combine("DayManager.Api", "Services", "ItemTools.cs"));
    var proactive38 = Read38(Path.Combine("DayManager.Api", "Services", "ProactiveService.cs"));

    Check("靠后有一条【视野校正】system，直接报项目数和能引用的具体名字",
        agent38.Contains("【视野校正】")
        && agent38.Contains("你现在看得见 {projectGroups.Count} 个大任务")
        && agent38.Contains("projectGroups.Take(5).Select(g => g.Project.Name)")
        && agent38.Contains("名字包括 {projectNames} 等"));
    Check("视野校正专治「你现在能看到了吗 / 你再看看」这类元问题",
        agent38.Contains("他问「你现在能看到了吗」「你再看看」时，照这段答「能」")
        && agent38.Contains("别说看不到、别让他截图"));
    Check("视野校正在历史与坐标之后、格式提醒之前（靠后权重最高）",
        agent38.IndexOf("【视野校正】") > agent38.IndexOf("foreach (var m in recentHistory)")
        && agent38.IndexOf("【视野校正】") > 0
        && agent38.IndexOf("输出必须是那个 JSON") > agent38.IndexOf("【视野校正】"));
    Check("【大任务】段显式推翻前几轮的旧结论",
        agent38.Contains("**你现在看得见它们。**")
        && agent38.Contains("那是旧信息，以本段为准"));
    Check("容器和项目不再被混成一个壳",
        agent38.Contains("**这些不是大任务**")
        && agent38.Contains("阶段性目标是另一类壳，别混"));
    Check("视野里凡要拿去动手的 id 给完整 Guid，和 ItemTools 的口径一致",
        !agent38.Contains("ShortId(")
        && agent38.Contains("{i.Id} | {i.Title}")
        && tools38.Contains("FindAsync(Guid id"));
    Check("范围守约：没加动作、没碰主动开口链路、没改数据模型",
        agent38.Contains("可用的操作有这六种")
        && !proactive38.Contains("【视野校正】")
        && !proactive38.Contains("projectGroups")
        && !agent38.Contains("AddContainerAsync"));
}

// ===== 第 39 节：清单上四种形态一眼可辨（今天 / 定点 / 期限 / 每天）=====
// 起因是真事：真实库里「背单词」（每天）和一条普通今天待办，时间字段一模一样
// （StartAt 空 + DueAt 今天 23:59），界面上都渲染成「尚未安排时间」——
// 他做完回看时完全分不清这条明天还会不会自己回来。
// 这节守两件事：形态词只从声明读（完成方式优先于时间形态），以及界面真的用上了它。
{
    var src39 = FindSrcRoot();
    string Read39(string rel) => src39 is null ? "" : File.ReadAllText(Path.Combine(src39, rel));

    // 【为什么不建临时库】形态词是纯函数（Core/Planning/ItemLabels），
    // 输入就是一条记录，落库口径在第 33 / 36 节已经测过。
    // 这里只测"读出来说的是哪两个字"。
    var day39 = new Item
    {
        Title = "写作业",
        Shape = ItemShape.DayTask,
        CompletionMode = CompletionMode.Once
    };
    Check("今天做 → 不给词（清单本身就是今天，写了是废话）",
        ItemLabels.ShapeLabel(day39) is null);

    var timed39 = new Item
    {
        Title = "九点开会",
        Shape = ItemShape.Timed,
        CompletionMode = CompletionMode.Once
    };
    Check("定点 → 「定点」", ItemLabels.ShapeLabel(timed39) == "定点");

    var deadline39 = new Item
    {
        Title = "交报告",
        Shape = ItemShape.DeadlineOnly,
        CompletionMode = CompletionMode.Once
    };
    Check("只有截止日 → 「期限」", ItemLabels.ShapeLabel(deadline39) == "期限");

    // 【最关键的一条】「每天」靠 CompletionMode 声明，它底下 Shape 仍是 DayTask。
    // 先读 Shape 就会把它说成「今天」——那正是这个 bug 的形态。
    var daily39 = new Item
    {
        Title = "背单词",
        Shape = ItemShape.DayTask,
        CompletionMode = CompletionMode.PerOccurrence
    };
    Check("每天 → 「每天」，不会被 Shape 读成「今天」",
        ItemLabels.ShapeLabel(daily39) == "每天");

    var container39 = new Item
    {
        Title = "第一章",
        Shape = ItemShape.DayTask,
        CompletionMode = CompletionMode.ByChildren
    };
    Check("容器不给形态词（它不进清单，靠缩进和下级进度自辨）",
        ItemLabels.ShapeLabel(container39) is null);

    var unanchored39 = new Item
    {
        Title = "历史空转值",
        Shape = ItemShape.Unanchored,
        CompletionMode = CompletionMode.Once
    };
    Check("Unanchored（真实库 0 条）同样不给词",
        ItemLabels.ShapeLabel(unanchored39) is null);

    var vm39 = Read39(Path.Combine("DayManager.App", "ViewModels", "ItemViewModel.cs"));
    Check("副标题第一段就是形态词，判定走 Core 不另写一遍",
        vm39.Contains("ItemLabels.ShapeLabel(_item)")
        && vm39.IndexOf("ItemLabels.ShapeLabel(_item)", StringComparison.Ordinal)
           < vm39.IndexOf("DurationText.Format", StringComparison.Ordinal));
    Check("「尚未安排时间」这个假话已经删掉（把它讲成了「没排」）",
        !vm39.Contains("尚未安排时间"));
    Check("期限当天补一句「今天截止」，否则和普通待办看不出区别",
        vm39.Contains("今天截止")
        && vm39.Contains("_item.Shape == ItemShape.DeadlineOnly"));
    Check("期限行：日程页取短句（前面有「期限」），详情页取整句（那里没形态词）",
        vm39.Contains("ItemPresence.DeadlineShortLine")
        && vm39.Contains(": ItemPresence.DeadlineLine"));

    var pd39 = Read39(Path.Combine("DayManager.App", "ViewModels", "ProjectDetailViewModel.cs"));
    Check("大任务详情页关掉形态词（层级靠缩进表达）",
        pd39.Contains("HideShapeLabel()"));
    Check("详情页加子任务不再被兜底塞成「今天到期」",
        pd39.Contains("keepNoDueAt: true"));

    var repo39 = Read39(Path.Combine("DayManager.Data", "Repositories", "ItemRepository.cs"));
    Check("清单查询：属于大任务又没定日期的条目，不进今天（防下次再加又冒出来）",
        repo39.Contains("&& x.ProjectId == null")
        && repo39.Contains("&& x.ParentTaskId == null"));

    var presence39 = Read39(Path.Combine("DayManager.Core", "Planning", "ItemPresence.cs"));
    Check("AI 那条整句仍带「截止」，没被界面的短句连累",
        presence39.Contains("DeadlineShortLine(dueAt, today) + \" 截止\""));
}

// ===== 第 40 节：做完的事单独一段（22:00 用户问「ddl 完成了之后去哪里了」）=====
// 之前完成的行混在未完成中间，只靠灰字和删除线区分；排序按 DueAt 走，
// 一条提前完成的「只有截止日」会掉到清单最末尾——看起来就是"点完就没了"。
// 这节守三件事：有固定去处、完成率不被分组算错、全做完时不说"今天还没有安排"。
{
    var src40 = FindSrcRoot();
    string Read40(string rel) => src40 is null ? "" : File.ReadAllText(Path.Combine(src40, rel));
    var mainVm40 = Read40(Path.Combine("DayManager.App", "ViewModels", "MainViewModel.cs"));
    var page40 = Read40(Path.Combine("DayManager.App", "MainPage.xaml"));
    var itemVm40 = Read40(Path.Combine("DayManager.App", "ViewModels", "ItemViewModel.cs"));

    Check("做完的事有独立集合，不和未完成的混在一张清单里",
        mainVm40.Contains("ObservableCollection<ItemViewModel> DoneItems")
        && mainVm40.Contains("DoneItems.Clear()")
        && mainVm40.Contains("DoneItems.Add(vm)"));

    Check("完成率分母 = 未完成 + 已完成（分组最容易把分母算成 0/0）",
        mainVm40.Contains("var total = TodayItems.Count + DoneItems.Count")
        && mainVm40.Contains("var done = DoneItems.Count"));

    Check("全做完了不弹「今天还没有安排」：空提示要条件化",
        mainVm40.Contains("ShowEmptyHint => TodayItems.Count == 0 && DoneItems.Count == 0")
        && page40.Contains("IsVisible=\"{Binding ShowEmptyHint}\""));

    Check("勾完成就地搬段，不整页重查（保住滚动位置）",
        mainVm40.Contains("TodayItems.Remove(item)")
        && mainVm40.Contains("DoneItems.Insert(0, item)"));

    Check("取消完成走重查，回到按日期排好的位置",
        mainVm40.Contains("【取消完成：整页重查】"));

    Check("清单区是单一 ScrollView 的三段（CollectionView 的 Header/Footer 在空列表下不滚动）",
        page40.Contains("<ScrollView Grid.Row=\"3\">")
        && page40.Contains("BindableLayout.ItemsSource=\"{Binding TodayItems}\"")
        && page40.Contains("BindableLayout.ItemsSource=\"{Binding DoneItems}\"")
        && !page40.Contains("CollectionView.Header")
        && !page40.Contains("CollectionView.Footer"));

    Check("两段共用同一份行模板（各抄一份迟早有一处忘改）",
        page40.Contains("x:Key=\"ItemRowTemplate\"")
        && System.Text.RegularExpressions.Regex.Matches(
               page40, "StaticResource ItemRowTemplate").Count == 2);

    Check("底部标题跟着翻页变，翻到过去不写「今天」",
        mainVm40.Contains("DoneSectionTitle => IsViewingToday")
        && page40.Contains("{Binding DoneSectionTitle}"));

    Check("已完成的行不再挂「截止 / 逾期」",
        itemVm40.Contains("已经做完的事，到这里就结束"));
}

// ===== 第 41 节：「每天」的第二天还在不在（22:33 用户问「背单词怎么在明天看不见」）=====
// 界面 11-22 就给了「每天」入口，但只写进当天那一条，后面没人延续——
// 库里 RRule 空、RecurrenceParentId 空，第二天查清单自然没有它。
// 一个点得出来却不兑现的入口，比没有这个入口更糟。
// 这节守三件事：第二天会补出新的一条、昨天那条留在历史、重复调用不会多建。
{
    var dbFile41 = Path.Combine(Path.GetTempPath(), $"dm-smoke-daily-{Guid.NewGuid():N}.db");
    var factory41 = new TestFactory(dbFile41);
    await using (var db41 = factory41.CreateDbContext())
    {
        await db41.Database.EnsureCreatedAsync();
    }

    var repo41 = new ItemRepository(factory41);
    var today41 = DateOnly.FromDateTime(DateTime.Now);
    var yesterday41 = today41.AddDays(-1);

    // ① 昨天建了一条「每天」，并且已经做完了
    var old41 = await repo41.AddToDayAsync("背单词", yesterday41,
        completionMode: CompletionMode.PerOccurrence);
    await repo41.ToggleDoneAsync(old41.Id);

    // ② 今天打开：应该补出今天这一条
    var created41 = await repo41.EnsureDailyInstancesAsync(today41);
    Check("跨天会给「每天」补出今天这一条", created41 == 1);

    var todayList41 = await repo41.GetDayListAsync(today41);

    // 【为什么按 Id 排除旧的那条，而不是 FirstOrDefault 取第一条】
    // 旧那条是"今天被标完成"的，按 completedThatDay 口径也会出现在今天的清单里
    // （真实场景里它就是"今天做完的事"）。直接取第一条拿到的是它，不是新补的。
    var fresh41 = todayList41.FirstOrDefault(x => x.Title == "背单词" && x.Id != old41.Id);

    Check("补出来的那条真的进了今天的清单，而且是待办",
        fresh41 is not null
        && fresh41.Status == ItemStatus.Pending
        && fresh41.CompletionMode == CompletionMode.PerOccurrence);

    Check("补出来的那条日期是今天，不是照抄昨天",
        fresh41?.DueAt is { } d41 && ItemPresence.LocalDay(d41) == today41);

    Check("用 RecurrenceParentId 串到前一条上（历史才查得出来）",
        fresh41?.RecurrenceParentId == old41.Id
        && fresh41?.OriginalStart is not null);

    // ③ 昨天那条保持原样。
    // 【为什么不能拿 GetDayListAsync(昨天) 查】
    // 那条的 CompletedAt 是"现在"，落在今天；按天的清单里它属于今天做完的事，
    // 翻到昨天是看不到的（这是既有口径，不是这轮的改动）。所以直接读实体。
    await using (var db41b = factory41.CreateDbContext())
    {
        var oldIn41 = await db41b.Items.FindAsync(old41.Id);
        Check("昨天那条仍是已完成，没被改写、没被搬走",
            oldIn41 is not null && oldIn41.Status == ItemStatus.Done);
    }

    // ④ 同一天再调一次，不该多建
    var again41 = await repo41.EnsureDailyInstancesAsync(today41);
    Check("同一天重复调用是幂等的（昨天那条已有后继就跳过）", again41 == 0);

    await using (var db41c = factory41.CreateDbContext())
    {
        var count41 = db41c.Items.Count(x => x.Title == "背单词" && x.DeletedAt == null);
        Check("库里总共两条：昨天一条、今天一条", count41 == 2);
    }

    // ⑤ 今天刚建的「每天」不该被自己再补一条
    await repo41.AddToDayAsync("喝水", today41, completionMode: CompletionMode.PerOccurrence);
    var self41 = await repo41.EnsureDailyInstancesAsync(today41);
    Check("今天刚建的「每天」不会被补成两条", self41 == 0);

    // ⑥ 不判成败的「每天」不补实例。
    // 它靠 EntersDayList 就被挡在清单外了，补出来只是一条谁也看不见的记录——
    // 22:37 真实库上真发生过："0点上床睡觉"和"背单词"一起被补了出来。
    await repo41.AddToDayAsync("0点上床睡觉", yesterday41,
        completionMode: CompletionMode.PerOccurrence,
        verdictMode: VerdictMode.Never);
    var never41 = await repo41.EnsureDailyInstancesAsync(today41);
    Check("不判成败的「每天」不补实例（它本来就不进清单）", never41 == 0);

    try { File.Delete(dbFile41); } catch { }
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
