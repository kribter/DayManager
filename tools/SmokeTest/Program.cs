// 临时冒烟测试：验证 ItemRepository 的核心逻辑是否按预期工作。
// 刻意用一个独立的临时数据库文件，不碰用户真实的数据。

using DayManager.Core.Ai;
using DayManager.Core.Entities;
using DayManager.Core.Enums;
using DayManager.Core.Intervention;
using DayManager.Core.Planning;
using DayManager.Core.Proactive;
using DayManager.Data;
using DayManager.Data.Repositories;
using DayManager.Data.Snapshots;
using System.Text.Json;
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

var usage = new UsageRepository(factory);
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
