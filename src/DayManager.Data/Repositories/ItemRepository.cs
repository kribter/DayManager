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
    /// 【"今天该显示什么"是这个软件最重要的判断题之一】
    /// 一共三类，每一类都对应一个明确的产品考虑：
    ///
    ///   ① 今天有具体时间的日程（StartAt 落在今天）
    ///      —— 不论完成与否都显示。完成了也要显示，因为"今天做到了什么"是正反馈的来源，
    ///         用户要是打开软件发现自己做过的事凭空消失了，挫败感会很重。
    ///
    ///   ② 截止时间在今天、或者已经逾期、但还没做完的待办
    ///      —— 这条最关键。如果逾期的任务不自动滚到今天，
    ///         它就永远停在过去某一天里，再也不会出现在你眼前。
    ///         那样软件的净效果是"帮你遗忘"，恰好放大了用户说的"想着等下再做然后就拖掉"。
    ///         所以逾期必须自动带进今天，这是对抗拖延的结构性保证。
    ///
    ///   ③ 既没有开始时间也没有截止时间的零散待办
    ///      —— 用户还没决定什么时候做。这类不能被藏着，否则等于没有。
    ///         将来 AI 排程上线后，会反过来推动用户给它们填上时间。
    ///
    /// 三类都不包含"已取消"，因为那是用户主动决定不做了。
    /// </summary>
    public async Task<List<Item>> GetTodayListAsync(DateOnly? day = null, CancellationToken ct = default)
    {
        var target = day ?? DateOnly.FromDateTime(DateTime.Now);

        var dayStart = StartOfDayAsLocalOffset(target);
        var dayEnd = dayStart.AddDays(1);

        await using var db = await _dbFactory.CreateDbContextAsync(ct);

        // 【为什么这里写成三个查询再拼接，而不是一个带 || 的大 Where】
        // 这不是为了好看，是被逼的：上面那三种条件一组合，
        // EF Core 就无法把它翻译成 SQL，运行时会直接抛
        // "could not be translated" —— 注意它是运行时错误，编译完全不报错。
        // 这类问题只有真的跑一遍才会暴露，光看代码是看不出来的。
        // 拆成三个各自简单的条件后，每个都能翻译成一条干净的 SQL，
        // 再由 Concat 在数据库里用 UNION ALL 拼起来（不会把数据拉到内存再合并）。

        // ① 今天有具体时间的日程（当天发生的事，做没做都要看到）
        var scheduledToday = db.Items.Where(x =>
            x.StartAt >= dayStart
            && x.StartAt < dayEnd
            && x.Status != ItemStatus.Cancelled);

        // ② 截止在今天或更早就到期、但还没做完的待办（含逾期）
        var dueSoonOrOverdue = db.Items.Where(x =>
            x.StartAt == null
            && x.DueAt != null
            && x.DueAt < dayEnd
            && x.Status != ItemStatus.Done
            && x.Status != ItemStatus.Cancelled);

        // ③ 什么时间都没定的零散待办
        var unscheduled = db.Items.Where(x =>
            x.StartAt == null
            && x.DueAt == null
            && x.Status != ItemStatus.Done
            && x.Status != ItemStatus.Cancelled);

        var items = await scheduledToday
            .Concat(dueSoonOrOverdue)
            .Concat(unscheduled)
            .ToListAsync(ct);

        // 【为什么不用 EF 直接 OrderBy，而是查回来再排序】
        // 排序键是"StartAt 优先、没有就 DueAt、再没有就 CreatedAt"这样的优先级组合，
        // 写成 SQL 会是一串难读的 CASE 表达式。
        // 而今日清单的数量级是几十条，在内存里排既清晰又快。
        // 这里的取舍是：可读性优先，性能留到数据量真的大了再优化。
        return items
            .OrderBy(x => x.StartAt != null ? 0 : x.DueAt != null ? 1 : 2)
            .ThenBy(x => x.StartAt ?? x.DueAt ?? x.CreatedAt)
            .ThenBy(x => x.SortOrder)
            .ToList();
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
        CancellationToken ct = default)
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
                item.CompletedAt = null;
                break;

            case ItemStatus.Cancelled:
                item.Status = ItemStatus.Cancelled;
                item.CompletedAt = null;
                break;
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
        var local = day.ToDateTime(TimeOnly.MinValue);
        var offset = TimeZoneInfo.Local.GetUtcOffset(local);
        return new DateTimeOffset(local, offset);
    }

    /// <summary>今天最后一刻（23:59:59.999），用作默认的截止时间。</summary>
    private static DateTimeOffset EndOfToday()
    {
        return StartOfDayAsLocalOffset(DateOnly.FromDateTime(DateTime.Now))
            .AddDays(1)
            .AddMilliseconds(-1);
    }
}
