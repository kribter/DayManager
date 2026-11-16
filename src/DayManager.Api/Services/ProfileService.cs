using DayManager.Core.Entities;
using DayManager.Core.Profile;
using DayManager.Data;
using DayManager.Data.Repositories;
using Microsoft.EntityFrameworkCore;

namespace DayManager.Api.Services;

/// <summary>
/// 从库里算出画像和趋势。
///
/// 【为什么画像和趋势分开两个方法】
/// 画像看最近 30 天，从明细现算——它要能立刻反映最近的变化。
/// 趋势从 daily_contexts 读已经固化下来的日结——它必须稳定，
/// 不能因为后来改了一条任务就自己动。
/// 两个目的相反，所以走两条路。
///
/// 【为什么画像的装配被搬到了 Data 层】
/// 时间页现在也要显示画像，而客户端没有后端的那种服务。
/// 装配（查哪几张表、窗口多少天、一条任务算哪天）留在两份代码里，
/// 迟早会出现"AI 说你启动 20 分钟、页面上写 35 分钟"这种谁都没错的分歧。
/// 装配写一次放在共享层，两端调同一个方法；这个类只剩"给后端用的门面"。
/// </summary>
public class ProfileService
{
    private readonly ProfileRepository _profile;
    private readonly IDbContextFactory<AppDbContext> _dbFactory;

    /// <summary>画像看最近多少天。复用共享层那个常量，免得两边说得不一样。</summary>
    public const int WindowDays = ProfileRepository.WindowDays;

    /// <summary>
    /// 趋势每个周期的长度。复用 Core 里的常量：
    /// 时间页上给他自己看的也是同一个周期，两边才不会打架。
    /// </summary>
    private const int TrendPeriodDays = TrendCalculator.PeriodDays;

    public ProfileService(
        ProfileRepository profile,
        IDbContextFactory<AppDbContext> dbFactory)
    {
        _profile = profile;
        _dbFactory = dbFactory;
    }

    /// <summary>最近一段时间的画像。</summary>
    public Task<UserProfile> BuildAsync(DateOnly today, CancellationToken ct = default) =>
        _profile.BuildAsync(today, ct);

    /// <summary>
    /// 最近七天对比前七天。
    ///
    /// 【为什么只从已结算的天里取】
    /// 没结算的天说明那天的数字还可能变（今天的更是还没过完），
    /// 拿它去比会出现"今天才过半天，所以这个周期看起来变差了"这种假退步。
    /// </summary>
    public async Task<IReadOnlyList<TrendItem>> BuildTrendAsync(
        DateOnly today,
        CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);

        var rows = await db.DailyContexts
            .Where(x => x.RolledUpAt != null)
            .ToListAsync(ct);

        var thisEnd = today.AddDays(-1);
        var thisStart = thisEnd.AddDays(-(TrendPeriodDays - 1));
        var lastEnd = thisStart.AddDays(-1);
        var lastStart = lastEnd.AddDays(-(TrendPeriodDays - 1));

        var before = TrendCalculator.BuildPeriod(
            lastStart,
            ToRows(rows, lastStart, lastEnd));

        var after = TrendCalculator.BuildPeriod(
            thisStart,
            ToRows(rows, thisStart, thisEnd));

        return TrendCalculator.Compare(before, after);
    }

    private static List<DailyRollupRow> ToRows(
        IEnumerable<DailyContext> rows,
        DateOnly from,
        DateOnly to) =>
        rows
            .Where(x => x.Date >= from && x.Date <= to)
            .Select(x => new DailyRollupRow(
                x.Date,
                x.PlannedCount,
                x.CompletedCount,
                x.AverageStartDelayMinutes,
                x.FocusMinutes))
            .ToList();
}
