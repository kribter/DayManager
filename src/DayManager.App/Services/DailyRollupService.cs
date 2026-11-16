using DayManager.Core.Entities;
using DayManager.Core.Enums;
using DayManager.Core.Planning;
using DayManager.Data.Repositories;

namespace DayManager.App.Services;

/// <summary>
/// 每天结束后，把这一天的客观情况固化到 daily_contexts 一行里。
///
/// 【为什么要有这一步：画像不能每次都从明细现算】
/// 理论上完成率、启动延迟都能从原始记录实时算，但那样每看一次趋势
/// 都要扫几万条明细，而且"当时的快照"会被后来的改动污染
/// （比如一条任务被改期了，历史那天的数据就变了）。
/// 固化下来，趋势线才是稳定的。
///
/// 【为什么放在客户端而不是后端】
/// 后端只在被请求的时候才活着，跑不了定时任务。
/// 客户端启动时补算缺失的天，天然就是"每天一次"。
///
/// 【为什么结算到"前一天"为止】
/// 今天还没过完，现在结算的话数字会偏小。
/// 用 DayBoundary 判定"今天"归属，凌晨那几个小时仍算前一天，
/// 所以凌晨 2 点不会把还没过完的一天结算掉。
/// </summary>
public class DailyRollupService
{
    private readonly ItemRepository _items;
    private readonly UsageRepository _usage;
    private readonly WorkSegmentRepository _work;
    private readonly DailyContextRepository _contexts;

    /// <summary>
    /// 第一次跑的时候往回看多少天。
    ///
    /// 【为什么要有上限】
    /// 没有上限的话第一次启动要扫全部历史。设个上限，
    /// 老数据放弃（反正那时候也没记任务计时，算出来的也不准），
    /// 从现在开始攒就够。之后每天只补一天。
    /// </summary>
    public const int MaxLookbackDays = 90;

    public DailyRollupService(
        ItemRepository items,
        UsageRepository usage,
        WorkSegmentRepository work,
        DailyContextRepository contexts)
    {
        _items = items;
        _usage = usage;
        _work = work;
        _contexts = contexts;
    }

    /// <summary>
    /// 补算所有还没结算过的天。返回这次结算了多少天。
    ///
    /// 【为什么失败要吞掉而不是抛出去】
    /// 它跑在启动路径上。日结失败最多是趋势缺一天，
    /// 不该因为这个让程序起不来——那才是真的坏事。
    /// </summary>
    public async Task<int> RollupMissingAsync(CancellationToken ct = default)
    {
        var now = DateTimeOffset.Now;
        var lastDay = DayBoundary.Resolve(now).AddDays(-1);

        var rolled = await _contexts.ListRolledUpAsync(ct);
        var already = rolled.ToHashSet();

        var latest = await _contexts.LatestRolledUpAsync(ct);

        // 往回看的地板：无论从哪天开始补，都不早于这个日子。
        var floor = lastDay.AddDays(-(MaxLookbackDays - 1));

        // 已经结算过 → 从那天之后开始补（稳态下每天只补一天）。
        // 从没结算过 → 往回看一段，把能补的补上。
        var start = latest is null ? floor : latest.Value.AddDays(1);

        // 上一次结算的日子太老时（比如隔了半年没开程序），不该从那天一路补到现在，
        // 那会循环几百次、把很早以前的空日子也翻一遍。地板兜住它。
        if (start < floor)
        {
            start = floor;
        }

        var count = 0;

        for (var day = start; day <= lastDay; day = day.AddDays(1))
        {
            if (already.Contains(day))
            {
                continue;
            }

            var items = await _items.GetDayListAsync(day, ct);
            var usage = await _usage.GetDaySummaryAsync(day, ct);
            var (from, to) = DayBoundary.RangeOf(day);
            var segments = await _work.GetOverlappingAsync(from, to, ct);

            // 这天什么痕迹都没有：不建行，也不标记结算。
            // 建一堆空行会把趋势图冲淡，而"没安排"本来也不需要一个 0 来证明。
            if (items.Count == 0 && usage.TotalSeconds == 0 && segments.Count == 0)
            {
                continue;
            }

            var planned = items.Count(x => x.Status != ItemStatus.Skipped);
            var completed = items.Count(x => x.Status == ItemStatus.Done);
            var cancelled = items.Count(x => x.Status == ItemStatus.Abandoned);

            await _contexts.UpsertAsync(
                day,
                row =>
                {
                    row.PlannedCount = planned;
                    row.CompletedCount = completed;
                    row.CancelledCount = cancelled;
                    row.AverageStartDelayMinutes = AverageDelay(items);

                    // 只存原始量，不存比值。
                    // "娱乐占多少"是加工出来的数，它的分母口径刚改过一次，
                    // 存下来等口径再变时就成了错数据；两个原始量随时能重算。
                    row.FocusMinutes = (usage.TotalSeconds - usage.EntertainmentSeconds) / 60;
                    row.EntertainmentMinutes = usage.EntertainmentSeconds / 60;
                },
                rolledUpAt: now,
                ct: ct);

            count++;
        }

        return count;
    }

    /// <summary>
    /// 这一天的平均启动延迟。
    ///
    /// 【为什么按天存平均值，而不是只留明细】
    /// 明细当然更全，但趋势要看的是"这个数在往哪走"，
    /// 一天一个数就够画出那条线。明细仍留在 items 里，需要时还能回去查。
    /// </summary>
    private static int? AverageDelay(IReadOnlyList<Item> items)
    {
        var delays = items
            .Where(x => x.StartAt is not null && x.StartedAt is not null)
            .Select(x => (x.StartedAt!.Value - x.StartAt!.Value).TotalMinutes)
            .Where(m => m >= 0 && m <= 24 * 60)
            .ToList();

        return delays.Count == 0 ? null : (int)delays.Average();
    }
}
