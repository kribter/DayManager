using DayManager.Core.Entities;
using DayManager.Core.Enums;
using Microsoft.EntityFrameworkCore;

namespace DayManager.Data.Repositories;

/// <summary>
/// 日程的读写封装。
///
/// 【为什么单独抽一层 Repository，而不是在界面代码里直接写 db.Items.Where(...)】
/// 三个理由，每一个在后面都会兑现：
///   1. 两端共用。手机和电脑要显示同样的"今日清单"，判定逻辑必须有唯一一处定义，
///      否则迟早出现"手机上显示了，电脑上没显示"这种最难查的问题。
///   2. 服务端也要用。M2 做同步时，API 接口会直接调用这里的方法，不重写一遍逻辑。
///   3. 状态流转的规则在这里集中保证。像"StartedAt 只能写一次"这种事，
///      如果散落在各个按钮的事件里，迟早有某个地方漏掉，而漏掉是静默的——数据错了你看不出来。
/// </summary>
public class ItemRepository
{
    private readonly IDbContextFactory<AppDbContext> _dbFactory;

    public ItemRepository(IDbContextFactory<AppDbContext> dbFactory)
    {
        _dbFactory = dbFactory;
    }

    /// <summary>
    /// 取某一天的清单（默认今天）。
    ///
    /// 【"某一天该显示什么"是这个软件最重要的判断题之一】
    /// 一共四类，每一类都对应一个明确的产品考虑：
    ///
    ///   ① 这一天有具体时间的日程（StartAt 落在这一天）
    ///      —— 不论完成与否都显示。完成了也要显示，因为"那一天做到了什么"是正反馈的来源，
    ///         用户要是打开软件发现自己做过的事凭空消失了，挫败感会很重。
    ///
    ///   ② 截止时间在这一天到期、但还没做完的待办
    ///      —— 今天还会额外带上逾期的旧账（见 ②补），不多叙述。
    ///
    ///   ②补 逾期自动滚到今天
    ///      —— 这条最关键。如果逾期的任务不自动滚到今天，
    ///         它就永远停在过去某一天里，再也不会出现在你眼前。
    ///         那样软件的净效果是"帮你遗忘"，恰好放大了用户说的"想着等下再做然后就拖掉"。
    ///         所以逾期必须自动带进当天，这是对抗拖延的结构性保证。
    ///
    ///         【为什么这一条只作用于"今天和过去"，不作用于未来】
    ///         翻到后天去看的日子，用户想看的是"那天有什么安排"，
    ///         而不是"我欠了多少旧账"。如果每一天的画面都挂着同一串没做完的事，
    ///         翻页就失去了意义——三天都长一个样，而且很快就学会了忽略它们。
    ///         旧账有它唯一正确的归宿：今天。
    ///
    ///   ③ 在这一天被标记为完成的记录（CompletedAt 落在这一天）
    ///      —— 这一条是为了补一个洞：② 的筛选条件里有"未完成"，
    ///         于是"今天到期、今天做完"的记录一勾完成就从清单里消失了。
    ///         消失恰恰违背 ① 里定的原则（做完了也要看得见），
    ///         并在回顾时表现为"我明明做了很多，日子却是空的"。
    ///
    ///   ④ 既没有开始时间也没有截止时间的零散待办
    ///      —— 用户还没决定什么时候做。这类不能被藏着，否则等于没有。
    ///         但【只有今天才显示】：它们的含义是"还没决定哪天做"，
    ///         今天才需要面对它们；如果连翻到明天、后天都能看到同一串，
    ///         那就变成每天重复出现的噪音，反而让人学会忽略它。
    ///
    /// 四类都不包含"已取消"，因为那是用户主动决定不做了。
    ///
    /// 【为什么要 GroupBy(Id) 去重】
    /// 同一条记录可能同时满足好几类（比如"今天开始、今天完成"既属于 ① 也属于 ③），
    /// 而 Concat 翻译成 SQL 是 UNION ALL——它不去重，会原样返回两行。
    /// 同一条任务在同一天里显示两次，是最难解释的那类 bug：数据没错，界面看着像错。
    /// 清单的量级是几十条，查回来按 Id 去重是最省事也最可靠的做法。
    /// </summary>
    public Task<List<Item>> GetTodayListAsync(DateOnly? day = null, CancellationToken ct = default)
    {
        return GetDayListAsync(day ?? DateOnly.FromDateTime(DateTime.Now), ct);
    }

    /// <summary>取指定日期的清单。翻到任意一天（过去或未来）都走这里。</summary>
    public async Task<List<Item>> GetDayListAsync(DateOnly day, CancellationToken ct = default)
    {
        var dayStart = StartOfDayAsLocalOffset(day);
        var dayEnd = dayStart.AddDays(1);
        var today = DateOnly.FromDateTime(DateTime.Now);
        var isFuture = day > today;
        var isToday = day == today;

        await using var db = await _dbFactory.CreateDbContextAsync(ct);

        // 【为什么这里写成四条查询再拼接，而不是一个带 || 的大 Where】
        // 这不是为了好看，是被逼的：上面那几种条件一组合，
        // EF Core 就无法把它翻译成 SQL，运行时会直接抛
        // "could not be translated" —— 注意它是运行时错误，编译完全不报错。
        // 这类问题只有真的跑一遍才会暴露，光看代码是看不出来的。
        // 拆成各自简单的条件后，每个都能翻译成一条干净的 SQL，
        // 再由 Concat 在数据库里用 UNION ALL 拼起来（不会把数据拉到内存再合并）。

        // ① 这一天有具体时间的日程（当天发生的事，做没做都要看到）
        var scheduledThatDay = db.Items.Where(x =>
            x.StartAt >= dayStart
            && x.StartAt < dayEnd
            && x.Status != ItemStatus.Abandoned);

        // ② 截止在这一天但还没做完的待办。
        //    今天和过去的日子要把逾期的旧账一起带上（否则欠账会永远停在过去某天），
        //    未来的日子只显示"那天到期"的东西——未来是给人排计划的，不是给人看账单的。
        var due = isFuture
            ? db.Items.Where(x =>
                x.StartAt == null
                && x.DueAt >= dayStart
                && x.DueAt < dayEnd
                && x.Status != ItemStatus.Done
                && x.Status != ItemStatus.Abandoned)
            : db.Items.Where(x =>
                x.StartAt == null
                && x.DueAt != null
                && x.DueAt < dayEnd
                && x.Status != ItemStatus.Done
                && x.Status != ItemStatus.Abandoned);

        // ③ 在这一天被做完的记录（做完也不从这一天的画面上消失）
        var completedThatDay = db.Items.Where(x =>
            x.CompletedAt != null
            && x.CompletedAt >= dayStart
            && x.CompletedAt < dayEnd
            && x.Status != ItemStatus.Abandoned);

        // ④ 什么时间都没定的零散待办（只在今天出现，理由见上面的说明）
        var unscheduled = isToday
            ? db.Items.Where(x =>
                x.StartAt == null
                && x.DueAt == null
                && x.Status != ItemStatus.Done
                && x.Status != ItemStatus.Abandoned)
            : db.Items.Where(x => false);

        // ⑤ 前一天被跳过的，这一天该自己回来。
        //
        //    【为什么必须有这一条】
        //    "今天先跳过（明天再来）"是一个承诺。兑现它的方式，是让它在明天的清单上出现。
        //    但上面四条规则没有一条兜得住它：它不在明天开始（StartAt 还是今天），
        //    不是明天到期，没做完，也不是"只在今天显示"的零散待办。
        //    结果是：今天点了跳过，明天翻过去一看——空的。
        //    用户不会知道是查询规则漏了一条，只会觉得"它消失了"，然后不再相信这个按钮。
        //
        //    【为什么只看"前一天"，而不是所有被跳过的】
        //    跳过是你对**明天**的承诺，不是对后天、大后天的。
        //    要是它赖在往后每一天的清单里，翻页就又变成"每天挂着同一串没做完的事"——
        //    那正是②里"未来不显示逾期旧账"要避免的东西。
        //    明天要是还没做，它就退回成普通逾期欠账，按②和④回到**今天**的清单上催你。
        var skippedBack = db.Items.Where(x =>
            x.Status == ItemStatus.Skipped
            && x.SkippedAt != null
            && x.SkippedAt >= dayStart.AddDays(-1)
            && x.SkippedAt < dayStart);

        var items = await scheduledThatDay
            .Concat(due)
            .Concat(completedThatDay)
            .Concat(unscheduled)
            .Concat(skippedBack)
            .ToListAsync(ct);

        // 【为什么不用 EF 直接 OrderBy，而是查回来再排序】
        // 排序键是"StartAt 优先、没有就 DueAt、再没有就 CreatedAt"这样的优先级组合，
        // 写成 SQL 会是一串难读的 CASE 表达式。
        // 而今日清单的数量级是几十条，在内存里排既清晰又快。
        // 这里的取舍是：可读性优先，性能留到数据量真的大了再优化。
        return items
            // 先用 GroupBy 去掉 UNION ALL 带来的重复行，否则同一条任务会显示两次
            .GroupBy(x => x.Id)
            .Select(g => g.First())
            // "跳过"的判定放在内存里做：
            // 它需要拿 SkippedAt 和"这一天"比较，写成自定义方法 EF 翻译不了，
            // 而一天的清单是几十条，内存过滤的代价可以忽略。
            .Where(x => !IsHiddenOn(x, day))
            .OrderBy(x => x.StartAt != null ? 0 : x.DueAt != null ? 1 : 2)
            .ThenBy(x => x.StartAt ?? x.DueAt ?? x.CreatedAt)
            .ThenBy(x => x.SortOrder)
            .ToList();
    }

    /// <summary>
    /// 这一条在这一天该不该被藏起来。
    ///
    /// 【两个"不做"的状态，行为完全相反】
    ///   放弃（Abandoned）→ 永远藏起来。那是用户承认不做了。
    ///   跳过（Skipped）  → 只在"跳过发生的那一天"藏起来，之后自动回来。
    ///
    /// 【为什么跳过的判定要拿时间和这一天比，而不是看状态就完事】
    /// 因为"跳过"只对那一天有效，而数据库里状态只有一个值。
    /// 今天跳过了，明天打开软件时，它应该是"还没做"，而不是"被跳过了"。
    /// 判定写成"跳过时刻 ≥ 这一天零点才算跳过态"之后，
    /// "明天照常回来"就是查出来的结果，不需要每天跑一个任务去把状态改回来——
    /// 少一个后台任务，就少一类"它今天没跑起来"的故障。
    ///
    /// 【为什么 SkippedAt 为空时算作不藏】
    /// 正常流程下 Skipped 状态一定配着 SkippedAt，
    /// 但万一数据不完整（同步、手工改库），藏起来就是静默丢失——
    /// 用户会以为任务凭空消失了。宁可多显示一条，也不能让它消失。
    /// </summary>
    private static bool IsHiddenOn(Item x, DateOnly day)
    {
        if (x.Status == ItemStatus.Abandoned)
        {
            return true;
        }

        if (x.Status != ItemStatus.Skipped || x.SkippedAt == null)
        {
            return false;
        }

        // 跳过发生在这天或之后 → 这一天它被跳过了，藏起来。
        // 跳过发生在更早的日子 → 那是以前的事，今天照常出现。
        return DateOnly.FromDateTime(x.SkippedAt.Value.LocalDateTime) >= day;
    }

    /// <summary>
    /// 这一天的统计。
    ///
    /// 【完成率的分母为什么这么算】
    /// 需求 2.2 定的口径：分母 = 已完成 + 未开始 + 进行中 + 已放弃，**跳过不计入**。
    /// 因为"跳过"是用户明确决定"今天不做它"，那天它就不该算数；
    /// 硬算进去只会让完成率无故变低，而低完成率恰恰是挫败感的来源——
    /// 我们是在对抗挫败，不是在制造它。
    /// 放弃必须计入分母：那是真的没做，藏起来就是自欺。
    /// </summary>
    public async Task<DayStats> GetDayStatsAsync(DateOnly day, CancellationToken ct = default)
    {
        var items = await GetDayListAsync(day, ct);

        // 【为什么"跳过几条"要另外查一次，不能用 items 数】
        // GetDayListAsync 已经把当天跳过的过滤掉了——它们不在清单里。
        // 但"今天跳过了 3 件"是要显示给用户看的：
        // 不说的话，他会以为那几件事凭空消失了，或者以为自己忘了做。
        // 所以这一条单独查，条件就是"跳过时刻落在这一天"。
        await using var db = await _dbFactory.CreateDbContextAsync(ct);

        var dayStart = StartOfDayAsLocalOffset(day);
        var dayEnd = dayStart.AddDays(1);

        var skippedToday = await db.Items.CountAsync(x =>
            x.Status == ItemStatus.Skipped
            && x.SkippedAt != null
            && x.SkippedAt >= dayStart
            && x.SkippedAt < dayEnd, ct);

        var done = items.Count(x => x.Status == ItemStatus.Done);

        return new DayStats
        {
            // 分母：清单里的都算。
            //   当天跳过的已经被 IsHiddenOn 过滤掉了，天然不进分母（口径见需求 2.2）；
            //   而"昨天跳过、今天回来"的那些**要**计入——它们今天是真的要做。
            //   这两句不写清楚，改查询规则的人很容易把后面这一类也一起排除掉，
            //   于是完成率会凭空变好，而实际上事情一件没做。
            Total = items.Count,
            Done = done,
            SkippedToday = skippedToday,
            TotalMinutes = items.Sum(x => x.EstimateMinutes ?? 0)
        };
    }

    /// <summary>某一天完成情况的快照。</summary>
    public sealed record DayStats
    {
        /// <summary>计入统计的条数（不含当天跳过的）。</summary>
        public int Total { get; init; }

        /// <summary>已完成的条数。</summary>
        public int Done { get; init; }

        /// <summary>这一天被跳过的条数（不进分母，但要让用户看见）。</summary>
        public int SkippedToday { get; init; }

        /// <summary>这一天的预计总用时。</summary>
        public int TotalMinutes { get; init; }

        /// <summary>完成率。没有要做的时为 0，不显示成 100% —— 空一天不是满分。</summary>
        public double CompletionRatio => Total == 0 ? 0 : (double)Done / Total;
    }

    /// <summary>
    /// 把一条记录加到指定的某一天。
    ///
    /// 【为什么单独加这个方法，而不是让界面层自己拼时间】
    /// "某一天的最后一刻"该算成几点几分，这类换算一旦在界面层重复一遍，
    /// 就会出现两处算法不一致的情况：一处按零点算，一处按 23:59 算。
    /// 表现出来的是"在明天加的任务，今天也算逾期"——日期对了，判断错了，极难查。
    /// 换算的答案只保留在 DateHelper 一处，这里只是把它用在"新增"这件事上。
    ///
    /// 给了具体几点时，这条的截止时间留空：已经有开始时间就不需要再挂一个截止，
    /// 两个都在反而说不清它到底算"到点要做"还是"当天要做完"。
    /// </summary>
    public async Task<Item> AddToDayAsync(string title, DateOnly day, TimeSpan? startTime = null,
        CancellationToken ct = default)
    {
        DateTimeOffset? startAt = startTime is null
            ? null
            : DateHelper.StartOfDay(day).Add(startTime.Value);

        DateTimeOffset? dueAt = startAt is null ? DateHelper.EndOfDay(day) : null;

        return await AddAsync(title, startAt, dueAt, null, ct);
    }

    /// <summary>
    /// 新建一条记录。
    ///
    /// 【为什么没给时间时默认截止到"今天结束"】
    /// 因为用户调研得到的结论是：没有具体时间的待办会被无限期推迟。
    /// 默认挂到今天，至少它会待在今天的清单上，
    /// 今晚回顾时你不得不面对它：要么做，要么明确改期，要么删掉——不能装看不见。
    /// </summary>
    public async Task<Item> AddAsync(string title, DateTimeOffset? startAt = null, DateTimeOffset? dueAt = null,
        Guid? projectId = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            throw new ArgumentException("标题不能为空", nameof(title));
        }

        await using var db = await _dbFactory.CreateDbContextAsync(ct);

        var item = new Item
        {
            Title = title.Trim(),
            StartAt = startAt,
            // 没指定任何时间时，默认截止到今晚最后一刻，保证它一定出现在今天的清单里
            DueAt = dueAt ?? (startAt == null ? EndOfToday() : null),

            // 挂到某个大任务下面。在大任务详情页里加子任务时传过来。
            //
            // 【为什么字符串 project（AI 拆解时用名字）和这里的 projectId 要分开】
            // AI 拆一件大事时还没见到项目实体，只能说"这八条都属于『开题报告』"；
            // 而人在详情页里加的时候，这一页打开的就是那个项目，id 是已知的。
            // 两条路最终写的是同一个字段，只是入口不同。
            ProjectId = projectId,

            // 记录创建时的时区，将来做重复日程跨时区展开时要用到。
            // TimeZoneInfo.Local.Id 在 Windows 上给的是中文 Windows 时区名（如 "China Standard Time"），
            // 严格来说应该转成 IANA 格式（"Asia/Shanghai"）。M1 阶段先原样存，
            // 等做重复日程（它会用到 Ical.Net，需要 IANA）时再统一转换。
            TimeZoneId = TimeZoneInfo.Local.Id
        };

        db.Items.Add(item);
        await db.SaveChangesAsync(ct);
        return item;
    }

    /// <summary>
    /// 改变一条记录的状态。这里是"启动延迟"指标的守门人。
    ///
    /// 【为什么要用 ??= 而不是 = 给 StartedAt 赋值】
    /// ??= 的意思是"只在它还是空的时候赋值"。
    /// 假设你在 9:00 点过一次"开始"，9:20 又手滑点了一下，
    /// 如果用普通赋值，StartedAt 会被刷新成 9:20，
    /// 算出来的启动延迟就从真实值缩水了二十分钟——数据静默失真，而且不可逆。
    /// 首次开始时间是一旦产生就不可修改的事实，所以只能写一次。
    /// </summary>
    public async Task<Item?> SetStatusAsync(Guid id, ItemStatus status, CancellationToken ct = default)
    {
        return await SetStatusAsync(id, status, null, ct);
    }

    /// <summary>
    /// 改变一条记录的状态。这里是"启动延迟"指标的守门人。
    ///
    /// 【为什么要用 ??= 而不是 = 给 StartedAt 赋值】
    /// ??= 的意思是"只在它还是空的时候赋值"。
    /// 假设你在 9:00 点过一次"开始"，9:20 又手滑点了一下，
    /// 如果用普通赋值，StartedAt 会被刷新成 9:20，
    /// 算出来的启动延迟就从真实值缩水了二十分钟——数据静默失真，而且不可逆。
    /// 首次开始时间是一旦产生就不可修改的事实，所以只能写一次。
    ///
    /// 【为什么跳过要单独收一个理由参数，而不是先设状态再改字段】
    /// 因为"状态变成跳过"和"记下为什么跳过"必须同时发生。
    /// 分成两步的话，中间任何一次异常都会留下一条"跳过了但没说为什么"的记录，
    /// 而这种记录对 AI 毫无价值——它看到的是"又没做"，看不到"今天有原因"。
    /// 一起提交，要么都成，要么都不成。
    /// </summary>
    /// <param name="skipReason">跳过的原因，只在设为"跳过"时生效。</param>
    public async Task<Item?> SetStatusAsync(Guid id, ItemStatus status, string? skipReason,
        CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);

        var item = await db.Items.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (item is null)
        {
            return null;
        }

        var now = DateTimeOffset.Now;

        switch (status)
        {
            case ItemStatus.InProgress:
                item.Status = ItemStatus.InProgress;
                item.StartedAt ??= now;
                break;

            case ItemStatus.Done:
                item.Status = ItemStatus.Done;
                // 没经过"开始"就直接勾完成的情况很常见（做完才想起来标记），
                // 这时 StartedAt 也不能空着，否则这一条的启动延迟数据就丢了。
                item.StartedAt ??= now;
                item.CompletedAt = now;
                break;

            case ItemStatus.Pending:
                item.Status = ItemStatus.Pending;
                // 特意不清除 StartedAt：你曾经开始过这个事实依然成立，
                // 撤销完成不代表撤销"曾经动过手"。
                // SkippedAt 也保留：它记录的是"最近一次跳过的时刻"，
                // 清掉就再也看不出这条被跳过过了（SkipCount 还在，但时间没了）。
                item.CompletedAt = null;
                break;

            case ItemStatus.Skipped:
                item.Status = ItemStatus.Skipped;
                item.SkippedAt = now;
                item.SkipCount++;
                item.SkipReason = string.IsNullOrWhiteSpace(skipReason) ? null : skipReason.Trim();
                item.CompletedAt = null;
                break;

            case ItemStatus.Abandoned:
                item.Status = ItemStatus.Abandoned;
                item.CompletedAt = null;
                // 放弃时清掉"这次跳过"的痕迹：它已经不是"今天不做"，而是"不做了"。
                // SkipCount 保留——那些次跳过是真实发生过的历史。
                item.SkippedAt = null;
                item.SkipReason = null;
                break;
        }

        await db.SaveChangesAsync(ct);
        return item;
    }

    /// <summary>
    /// 改期：把一条记录挪到另一天（可以给具体几点，也可以只给日期）。
    ///
    /// 【为什么单独做一个方法，而不是让调用方自己改 StartAt / DueAt】
    /// "改期"看起来只是改两个字段，但它藏着一个规则：
    /// **给了具体几点就清掉截止时间，只给日期就清掉开始时间。**
    /// 两个都在的时候，这条记录到底算"到点要做"还是"当天要做完"是说不清的，
    /// 而 AI 在改期时最容易犯的错就是只改一个、留下另一个变成互相矛盾的脏数据。
    /// 规则收在这里，AI 那边只需要说"挪到哪天、几点"。
    /// </summary>
    public async Task<Item?> RescheduleAsync(Guid id, DateOnly day, TimeSpan? time = null,
        CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);

        var item = await db.Items.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (item is null)
        {
            return null;
        }

        if (time is null)
        {
            // 只给日期：它是"那天要做完"，给一个截止时刻，不留具体开始时间
            item.StartAt = null;
            item.DueAt = DateHelper.EndOfDay(day);
        }
        else
        {
            // 给了几点：它是"那天那个时刻开始"，不再需要截止时间
            item.StartAt = DateHelper.StartOfDay(day).Add(time.Value);
            item.DueAt = null;
        }

        await db.SaveChangesAsync(ct);
        return item;
    }

    public async Task<Item?> RenameAsync(Guid id, string title, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            throw new ArgumentException("标题不能为空", nameof(title));
        }

        await using var db = await _dbFactory.CreateDbContextAsync(ct);

        var item = await db.Items.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (item is null)
        {
            return null;
        }

        item.Title = title.Trim();
        await db.SaveChangesAsync(ct);
        return item;
    }

    /// <summary>
    /// 改「最小启动步骤」——两分钟能做完的第一步。
    ///
    /// 【为什么值得单独开一个方法，而不是让调用方自己改字段】
    /// 跟改标题一个道理：真正的规则是"空白就当成没有"和"不能超长"，
    /// 写在外面迟早有一处忘了去空白，存进去一个空格，
    /// 界面上就多出一行空的「第一步：」。
    ///
    /// 【为什么允许传 null 把它清掉】
    /// AI 拆解时给每条都填了第一步，但填得不好的情况也有
    /// （比如 AI 给的"第一步"其实还是整件事）。
    /// 那时用户要能把它去掉，而不是被迫天天看一句没用的话。
    /// </summary>
    public async Task<Item?> SetMinimalStepAsync(Guid id, string? value,
        CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);

        var item = await db.Items.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (item is null)
        {
            return null;
        }

        var trimmed = string.IsNullOrWhiteSpace(value) ? null : value.Trim();

        item.MinimalStep = trimmed is { Length: > MaxMinimalStepLength }
            ? trimmed[..MaxMinimalStepLength]
            : trimmed;

        await db.SaveChangesAsync(ct);
        return item;
    }

    /// <summary>「最小启动步骤」的长度上限，与 AppDbContext 里的 HasMaxLength 保持一致。</summary>
    private const int MaxMinimalStepLength = 500;

    /// <summary>
    /// 软删除。
    ///
    /// 【为什么不是真的 Remove】
    /// 见 SyncEntityBase.DeletedAt 的说明：真删掉的话，将来手机和电脑同步时，
    /// 另一台设备根本不知道"这条被删了"，会把它当成从来没存在过，于是又同步回来。
    /// 留下墓碑，删除这个动作才能被传播。
    /// </summary>
    public async Task SoftDeleteAsync(Guid id, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);

        var item = await db.Items.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (item is null)
        {
            return;
        }

        item.DeletedAt = DateTimeOffset.Now;
        await db.SaveChangesAsync(ct);
    }

    // ---------- 日期换算的小工具 ----------

    /// <summary>
    /// 把"某一天"换算成一个确切的时刻。
    ///
    /// 【为什么要折腾这么一下】
    /// DateTimeOffset 必须带偏移量才能表示一个确切瞬间，而单纯的"2026-09-27"没有偏移。
    /// 所以显式用本地时区把这一天的零点补上偏移，得到类似
    /// 2026-09-27T00:00:00+08:00 的值，才能拿去和数据库里的时间比大小。
    ///
    /// 【时区问题在哪一层被解决】
    /// 在 AppDbContext 里：所有 DateTimeOffset 入库前都会转成 UTC 毫秒。
    /// 也就是说数据库里存的是绝对时刻，两台不同时区的设备比大小都不会出错。
    /// 这里只需要负责"把本地的一天"翻译成一个正确的瞬间即可，其余不必操心。
    /// （如果不做那层转换，跨时区比较会出错——详见 AppDbContext 里的说明。）
    /// </summary>
    private static DateTimeOffset StartOfDayAsLocalOffset(DateOnly day)
    {
        // 实现统一放在 DateHelper 里，理由见那里的说明。
        return DateHelper.StartOfDay(day);
    }

    /// <summary>今天最后一刻（23:59:59.999），用作默认的截止时间。</summary>
    private static DateTimeOffset EndOfToday()
    {
        return DateHelper.EndOfDay(DateOnly.FromDateTime(DateTime.Now));
    }
}
