using DayManager.Core.Entities;
using DayManager.Core.Enums;
using Microsoft.EntityFrameworkCore;

namespace DayManager.Data.Repositories;

/// <summary>
/// 设备使用时长的读写与统计。
///
/// 【为什么统计在仓储里做，不把原始数据捞出来在界面上算】
/// 一年的会话有几万条，全捞进内存再分组，界面每刷新一次就要算一遍。
/// 让数据库做分组求和，只把几行结果传回来——SQL 的 GROUP BY 就是干这个的。
/// </summary>
public class UsageRepository
{
    private readonly IDbContextFactory<AppDbContext> _factory;

    public UsageRepository(IDbContextFactory<AppDbContext> factory)
    {
        _factory = factory;
    }

    /// <summary>
    /// 开始一段会话。采集器发现"前台换了个应用"时调用。
    /// </summary>
    public async Task<UsageSession> StartAsync(
        DevicePlatform platform,
        string appId,
        string? appName,
        string? windowTitle,
        bool isEntertainment,
        DateTimeOffset at,
        string? deviceId = null,
        CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);

        var session = new UsageSession
        {
            Platform = platform,
            AppId = appId,
            AppName = appName,
            WindowTitle = windowTitle,
            IsEntertainment = isEntertainment,
            StartAt = at,
            EndAt = at,
            DurationSeconds = 0,
            DeviceId = deviceId
        };

        db.UsageSessions.Add(session);
        await db.SaveChangesAsync(ct);
        return session;
    }

    /// <summary>
    /// 把一段会话的结束时间推到此刻。**不动 DurationSeconds**。
    ///
    /// 【为什么每次采样都推 EndAt，而不是只在结束时写一次】
    /// 程序被关掉、电脑被合上的那一刻，是没有机会写"结束"的。
    /// 如果只在正常切换时才写，最后一段会话就会停在几小时前——
    /// 下次开机看到"昨天用了 14 小时"，而这个数把关机的时间也算成了使用。
    /// 每次采样都推一下，关机时它停在最后一次采样，误差只有几秒。
    ///
    /// 【为什么这里不再顺手算 DurationSeconds = EndAt - StartAt】
    /// 那样算出来的是"墙上的钟走了多久"，不是"人真的用了多久"。
    /// 中间去吃饭的十分钟、合盖睡眠的八小时都会被算进去。
    /// 时长只由 AddActiveSecondsAsync 逐次累加，这个数才是干净的。
    /// </summary>
    public async Task ExtendAsync(Guid sessionId, DateTimeOffset at, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);

        var session = await db.UsageSessions.FirstOrDefaultAsync(x => x.Id == sessionId, ct);
        if (session is null)
        {
            return;
        }

        session.EndAt = at;
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// 给一段会话累加"确实在用"的秒数。
    ///
    /// 【为什么不直接用 EndAt - StartAt 算时长】
    /// 那是墙上的钟走了多久，不是人真的用了多久。
    /// 人离开十分钟再回来用同一个软件，按时间差算就多出十分钟，
    /// 一天下来"用了 9 小时"里可能有两个小时是在吃饭。
    /// 而这份数据是干预引擎和 AI 排期的唯一输入——数字虚高，
    /// 后面的每一句劝告都建立在谎话上。
    /// 所以采集器每次采样只把**这一次采样间隔**加进去，空档自然不算。
    /// </summary>
    public async Task AddActiveSecondsAsync(
        Guid sessionId,
        int seconds,
        DateTimeOffset at,
        string? windowTitle = null,
        CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);

        var session = await db.UsageSessions.FirstOrDefaultAsync(x => x.Id == sessionId, ct);
        if (session is null)
        {
            return;
        }

        // EndAt 一直往前推：程序被强杀时没有机会写"结束"，
        // 结束时间停在哪里，这段就记到哪里，误差最多一个采样间隔。
        session.EndAt = at;

        if (seconds > 0)
        {
            session.DurationSeconds += seconds;
        }

        // 窗口标题跟着刷新：同一个应用里换了文件/页面，取最新的那个才有参考价值。
        if (!string.IsNullOrWhiteSpace(windowTitle))
        {
            session.WindowTitle = windowTitle;
        }

        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// 结束一段会话（前台切走了，或者人离开了）。
    /// </summary>
    public async Task EndAsync(Guid sessionId, DateTimeOffset at, CancellationToken ct = default)
    {
        await ExtendAsync(sessionId, at, ct);
    }

    /// <summary>
    /// 把上次没有正常结束的会话收尾。程序启动时调用。
    ///
    /// 【为什么需要它】
    /// 上次可能是崩溃、被任务管理器杀掉、或者直接关机。
    /// 这些情况下内存里"当前会话"的 id 一起消失了，
    /// 那条记录的 EndAt 停在关机前的最后一次采样——这已经是对的（见 ExtendAsync），
    /// 这里只是把它标记清楚，避免以后误认为"还开着"。
    /// </summary>
    public async Task<int> CloseOrphansAsync(DateTimeOffset startedAt, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);

        // 只有"结束时间早于本次启动很久"的才算孤儿；
        // 刚刚还在写的那一条不算（可能是上一次进程还没完全退出）。
        var cutoff = startedAt.AddMinutes(-5);

        var orphans = await db.UsageSessions
            .Where(x => x.EndAt < cutoff && x.DurationSeconds > 0)
            .OrderByDescending(x => x.EndAt)
            .Take(50)
            .ToListAsync(ct);

        // 时长已经在每次采样时算好了，这里没有要改的数据。
        // 保留返回条数是为了让调用方能记日志——排查"为什么昨天的时长不对"时要看它。
        return orphans.Count;
    }

    /// <summary>
    /// 某一天里，每个应用各用了多久（秒）。按用得多的排前面。
    ///
    /// 【为什么直接转给 GetRangeAsync】
    /// 日/周/月/年四个跨度要的其实是同一件事，只是区间不同。
    /// 分成两套算法的话，迟早会出现"日视图的数字和周视图里那天的数字对不上"——
    /// 那时候没人能一眼看出是哪一套算错了。算的地方只留一处。
    /// </summary>
    public async Task<List<AppUsage>> GetAppTotalsAsync(DateOnly day, CancellationToken ct = default)
    {
        var range = await GetRangeAsync(day, day.AddDays(1), ct);
        return range.Apps.ToList();
    }

    /// <summary>
    /// 某一天的总时长、娱乐时长，以及第一个/最后一个会话的时间。
    /// 后面两个是给"作息推断"用的：末次活跃可以近似当作入睡时间。
    /// </summary>
    public async Task<DayUsageSummary> GetDaySummaryAsync(DateOnly day, CancellationToken ct = default)
    {
        var range = await GetRangeAsync(day, day.AddDays(1), ct);

        return new DayUsageSummary(
            range.TotalSeconds,
            range.EntertainmentSeconds,
            range.FirstAt,
            range.LastAt,
            range.SessionCount);
    }

    /// <summary>
    /// 一段区间（含头不含尾）里的全部使用统计：总量、按天、按应用。
    ///
    /// 【为什么日/周/月/年都走这一个方法】
    /// 四个跨度要的其实是同一件事，只是区间不同。
    /// 各写一套的话，迟早出现"日视图说今天 3 小时、周视图里今天只有 2.5 小时"，
    /// 而那时没人能一眼看出是哪一套算错了。
    ///
    /// 【为什么只查一次库，分组都在内存里做】
    /// 区间最长是一年，会话条数量级在几千到几万，投影只取六个字段，
    /// 一次查完再分组，比按天/按应用/按小时查三遍要快，也不会出现三次结果互相矛盾。
    /// 真正的瓶颈从来不是这点内存，而是"数字对不上"带来的不信任。
    /// </summary>
    public async Task<UsageRange> GetRangeAsync(DateOnly from, DateOnly toExclusive, CancellationToken ct = default)
    {
        if (toExclusive <= from)
        {
            toExclusive = from.AddDays(1);
        }

        await using var db = await _factory.CreateDbContextAsync(ct);

        var start = DayRange(from).Start;
        var end = DayRange(toExclusive).Start;

        var rows = await db.UsageSessions
            .Where(x => x.StartAt >= start && x.StartAt < end && x.DurationSeconds > 0)
            .Select(x => new
            {
                x.AppId,
                x.AppName,
                x.StartAt,
                x.EndAt,
                x.DurationSeconds,
                x.IsEntertainment
            })
            .ToListAsync(ct);

        if (rows.Count == 0)
        {
            return UsageRange.Empty;
        }

        // 【分组前必须先转本地时间】库里是 UTC 毫秒，读回来偏移是 0。
        // 不转的话，晚上 8 点以后的使用会被算到"第二天"——跨天统计全错位。
        var days = rows
            .GroupBy(r => DateOnly.FromDateTime(r.StartAt.ToLocalTime().DateTime))
            .Select(g => new UsageSlice(
                g.Key,
                g.Sum(x => x.DurationSeconds),
                g.Where(x => x.IsEntertainment).Sum(x => x.DurationSeconds),
                g.Count()))
            .OrderBy(x => x.Day)
            .ToList();

        // 【为什么按 AppId 分组，显示名却取最近一次的那个】
        // 同一个软件改过名字、或者取不到名字时退化成进程名，按名字分组会把它拆成两行；
        // 而 AppId 是稳定的身份。展示名取最近的，才贴近用户现在的认知。
        var apps = rows
            .GroupBy(r => r.AppId)
            .Select(g =>
            {
                var latestName = g.OrderByDescending(x => x.EndAt).Select(x => x.AppName).FirstOrDefault();
                return new AppUsage(
                    g.Key,
                    string.IsNullOrWhiteSpace(latestName) ? g.Key : latestName!,
                    g.Sum(x => x.DurationSeconds),
                    g.Any(x => x.IsEntertainment));
            })
            .OrderByDescending(x => x.Seconds)
            .ToList();

        return new UsageRange(
            TotalSeconds: rows.Sum(x => x.DurationSeconds),
            EntertainmentSeconds: rows.Where(x => x.IsEntertainment).Sum(x => x.DurationSeconds),
            SessionCount: rows.Count,
            // 同样是 UTC 转本地：末次活跃要当成"几点睡的"，差 8 小时就是另一个故事了。
            FirstAt: rows.Min(x => x.StartAt).ToLocalTime(),
            LastAt: rows.Max(x => x.EndAt).ToLocalTime(),
            Days: days,
            Apps: apps);
    }

    /// <summary>
    /// 某一天里每个小时各用了多少秒（24 个数字，下标就是小时）。
    /// 时间页画"什么时候最有效率"的柱状图要用。
    /// </summary>
    public async Task<int[]> GetHourlyAsync(DateOnly day, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);

        var (start, end) = DayRange(day);
        var result = new int[24];

        // 【为什么要拉回来在内存里摊，而不是让数据库按小时分组】
        // 一段会话会横跨好几个小时（比如从 10:50 用到 11:30），
        // 它不属于任何一个小时，必须按落在每个小时里的**比例**拆开。
        // 这种"把区间切分到时间桶里"的算法 SQL 写不出来，硬写会非常难读，
        // 而一天的数据量只有几十条，在内存里算反而更快也更清楚。
        var sessions = await db.UsageSessions
            .Where(x => x.StartAt >= start && x.StartAt < end && x.DurationSeconds > 0)
            .ToListAsync(ct);

        // 【桶必须按"本地的几点"分，不是按 UTC 的几点】
        // 库里时间统一存成 UTC 毫秒，读回来的偏移量是 0。
        // 这里如果直接取 .Hour，十一点看的东西会被记到凌晨三点那个柱子上——
        // 而"几点最有效率"这张图正是要回答作息问题的，错位就等于全错。
        var dayStart = day.ToDateTime(TimeOnly.MinValue);
        var dayEnd = dayStart.AddDays(1);

        foreach (var s in sessions)
        {
            var cursor = s.StartAt.ToLocalTime().DateTime;
            if (cursor < dayStart)
            {
                cursor = dayStart;
            }

            var stop = s.EndAt.ToLocalTime().DateTime;
            if (stop > dayEnd)
            {
                stop = dayEnd;
            }

            while (cursor < stop)
            {
                // 当前小时还剩多少
                var hourEnd = new DateTime(cursor.Year, cursor.Month, cursor.Day, cursor.Hour, 0, 0).AddHours(1);
                var sliceEnd = stop < hourEnd ? stop : hourEnd;

                result[cursor.Hour] += (int)(sliceEnd - cursor).TotalSeconds;
                cursor = sliceEnd;
            }
        }

        return result;
    }

    /// <summary>
    /// 删除某一天的全部会话（测试用，以及"清空我的使用记录"这种隐私需求）。
    /// 软删：墓碑留着，同步时别的设备才能收到"这些删了"。
    /// </summary>
    public async Task<int> DeleteDayAsync(DateOnly day, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);

        var (start, end) = DayRange(day);
        var rows = await db.UsageSessions
            .Where(x => x.StartAt >= start && x.StartAt < end)
            .ToListAsync(ct);

        var now = DateTimeOffset.Now;
        foreach (var r in rows)
        {
            r.DeletedAt = now;
        }

        await db.SaveChangesAsync(ct);
        return rows.Count;
    }

    private static (DateTimeOffset Start, DateTimeOffset End) DayRange(DateOnly day)
    {
        var start = new DateTimeOffset(day.ToDateTime(TimeOnly.MinValue), TimeZoneInfo.Local.GetUtcOffset(day.ToDateTime(TimeOnly.MinValue)));
        return (start, start.AddDays(1));
    }
}

/// <summary>一个应用在一天里用了多久。</summary>
public record AppUsage(string AppId, string DisplayName, int Seconds, bool IsEntertainment);

/// <summary>一天（或某个时间桶）里的使用情况。</summary>
public record UsageSlice(DateOnly Day, int TotalSeconds, int EntertainmentSeconds, int SessionCount);

/// <summary>一段时间里的全部统计。时间页的四张图都从这一份数据里出。</summary>
public record UsageRange(
    int TotalSeconds,
    int EntertainmentSeconds,
    int SessionCount,
    DateTimeOffset? FirstAt,
    DateTimeOffset? LastAt,
    IReadOnlyList<UsageSlice> Days,
    IReadOnlyList<AppUsage> Apps)
{
    public static UsageRange Empty { get; } = new(
        0, 0, 0, null, null, Array.Empty<UsageSlice>(), Array.Empty<AppUsage>());
}

/// <summary>一天的汇总。</summary>
public record DayUsageSummary(
    int TotalSeconds,
    int EntertainmentSeconds,
    DateTimeOffset? FirstAt,
    DateTimeOffset? LastAt,
    int SessionCount);
