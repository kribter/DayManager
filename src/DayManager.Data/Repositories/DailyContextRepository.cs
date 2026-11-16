using DayManager.Core.Entities;
using Microsoft.EntityFrameworkCore;

namespace DayManager.Data.Repositories;

/// <summary>
/// 每日上下文的读写。
///
/// 【为什么现在才需要这个仓储】
/// 以前只有后端写它（存晚间总结一句话），直接在服务里开 DbContext 就够了。
/// 现在日结要由客户端来做（后端只在被请求时才活着，不适合跑定时任务），
/// 两端都要读写它，就得有个正规的仓储放在共享层。
/// </summary>
public class DailyContextRepository
{
    private readonly IDbContextFactory<AppDbContext> _factory;

    public DailyContextRepository(IDbContextFactory<AppDbContext> factory)
    {
        _factory = factory;
    }

    /// <summary>取某一天的上下文。没有返回空。</summary>
    public async Task<DailyContext?> GetAsync(DateOnly day, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        return await db.DailyContexts.FirstOrDefaultAsync(x => x.Date == day, ct);
    }

    /// <summary>
    /// 列出已经结算过的天。
    /// </summary>
    public async Task<List<DateOnly>> ListRolledUpAsync(CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);

        return await db.DailyContexts
            .Where(x => x.RolledUpAt != null)
            .Select(x => x.Date)
            .ToListAsync(ct);
    }

    /// <summary>
    /// 取最近一次已结算的日子。没有就返回 null。
    ///
    /// 【为什么 Select 里要转成 DateOnly?】
    /// 直接 Select(x => x.Date) 选出来的是**非空** DateOnly，
    /// 那么在"一行都没有"的时候，FirstOrDefaultAsync 返回的是 default(DateOnly)，
    /// 也就是 0001-01-01 —— 不是 null。
    /// 调用方于是拿到一个"最早结算日是公元元年"的结果，
    /// 从那天开始补算：第一次换算本地零点就把 UTC 时间推到年份 0 以外，
    /// 直接抛 ArgumentOutOfRangeException。
    /// 这个坑的阴险之处在于编译完全通过，只有真的没有数据时才会炸。
    /// 转成可空类型，没有行就是 null，语义才对。
    /// </summary>
    public async Task<DateOnly?> LatestRolledUpAsync(CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);

        return await db.DailyContexts
            .Where(x => x.RolledUpAt != null)
            .OrderByDescending(x => x.Date)
            .Select(x => (DateOnly?)x.Date)
            .FirstOrDefaultAsync(ct);
    }

    /// <summary>
    /// 取出一个区间内的日结行（含没结算过的）。
    ///
    /// 【为什么连没结算的也一起给】
    /// 画像那边要自己判断"这天算过没有"，
    /// 让调用方拿到全部再筛，比仓储替它做决定清楚。
    /// </summary>
    public async Task<List<DailyContext>> ListAsync(
        DateOnly from,
        DateOnly to,
        CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);

        return await db.DailyContexts
            .Where(x => x.Date >= from && x.Date <= to)
            .ToListAsync(ct);
    }

    /// <summary>
    /// 写入某一天的统计。已有的覆盖，没有的新建。
    /// </summary>
    /// <param name="day">哪一天。</param>
    /// <param name="fill">往这行上填什么。留一个回调是为了让"填哪些字段"由调用方决定。</param>
    /// <param name="rolledUpAt">结算时刻。传了就打上"已结算"标记。</param>
    public async Task UpsertAsync(
        DateOnly day,
        Action<DailyContext> fill,
        DateTimeOffset? rolledUpAt = null,
        CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);

        var row = await db.DailyContexts.FirstOrDefaultAsync(x => x.Date == day, ct);

        if (row is null)
        {
            row = new DailyContext { Date = day };
            db.DailyContexts.Add(row);
        }

        fill(row);

        if (rolledUpAt is not null)
        {
            row.RolledUpAt = rolledUpAt;
        }

        await db.SaveChangesAsync(ct);
    }
}
