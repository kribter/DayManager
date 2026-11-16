using DayManager.Core.Entities;
using DayManager.Core.Enums;
using DayManager.Core.Planning;
using DayManager.Core.Profile;
using Microsoft.EntityFrameworkCore;

namespace DayManager.Data.Repositories;

/// <summary>
/// 画像的数据装配：从明细里捞出样本，交给 Core 的纯函数去算。
///
/// 【为什么要单独一层，而不是让客户端和后端各查一遍】
/// 画像的口径特别容易漂——窗口多少天、哪张表的哪一列算"在做事"、
/// 一条任务算哪一天。两边各写一份的话，会出现"AI 说你启动平均 20 分钟，
/// 你自己看到的页面写着 35 分钟"，而且谁都没错、只是口径不同，
/// 这种分歧极难发现。装配只写一次，两端调同一个方法。
///
/// 【为什么放在 Data 而不是 Api】
/// 后端只在被请求时才活着，时间页要自己算。放在共享层，
/// 将来的 Android 端也是同一句调用。
/// </summary>
public class ProfileRepository
{
    /// <summary>
    /// 画像看最近多少天。
    ///
    /// 【为什么不看全部历史】
    /// 三个月前的他不是现在的他。全部历史一起算的话，
    /// 一次"那阵子状态特别好"会把现在的判断拽走，
    /// 而且越攒越难被新数据推翻——那正是画像最该避免的固化。
    /// </summary>
    public const int WindowDays = 30;

    private readonly IDbContextFactory<AppDbContext> _factory;

    public ProfileRepository(IDbContextFactory<AppDbContext> factory)
    {
        _factory = factory;
    }

    /// <summary>算出"今天为止"的画像。</summary>
    public async Task<UserProfile> BuildAsync(DateOnly today, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);

        var (from, _) = DayBoundary.RangeOf(today.AddDays(-WindowDays));
        var (_, to) = DayBoundary.RangeOf(today);

        var items = await db.Items
            .Where(x => x.DeletedAt == null)
            .Where(x => (x.StartAt >= from && x.StartAt < to)
                        || (x.CompletedAt >= from && x.CompletedAt < to))
            .ToListAsync(ct);

        var segments = await db.WorkSegments
            .Where(x => x.DeletedAt == null)
            .Where(x => x.StartAt < to && (x.EndAt == null || x.EndAt >= from))
            .ToListAsync(ct);

        var usage = await db.UsageSessions
            .Where(x => x.DeletedAt == null && !x.IsEntertainment)
            // 屏幕会话的 EndAt 一定有值（会话结束时就写上了），
            // 不像计时段那样存在"还在走、右端点为空"的情况。
            .Where(x => x.StartAt < to && x.EndAt >= from)
            .ToListAsync(ct);

        var now = DateTimeOffset.Now;

        var samples = items
            .Select(x => new WorkSample(
                PlannedStart: x.StartAt,
                ActualStart: x.StartedAt,
                EstimateMinutes: x.EstimateMinutes,
                ActualMinutes: x.ActualMinutes,
                CompletedAt: x.CompletedAt,
                Skipped: x.Status == ItemStatus.Skipped,
                SkipReason: x.SkipReason))
            .ToList();

        var days = items
            .GroupBy(x => DayOf(x))
            .Select(g => new DayRate(
                g.Key,
                g.Count(x => x.Status != ItemStatus.Skipped),
                g.Count(x => x.Status == ItemStatus.Done)))
            .ToList();

        // 做事的时间 = 自己按了启动的段 + 屏幕前的非娱乐段。
        // 后者要按时长补出右端点：库里只有 StartAt + DurationSeconds，
        // 没有位置信息（这也是并集只能用起止时刻的原因）。
        var active = new List<ActiveSpan>();

        active.AddRange(segments.Select(x =>
            new ActiveSpan(x.StartAt, x.EndAt ?? now)));

        // 屏幕那段用"开始 + 净时长"补出右端点。
        // 这是近似：一次会话里不足三分钟的小空档被算成在做事了。
        // 偏差方向是"看起来更努力一点"——对"哪个小时他在动"这个用途够用，
        // 而且宁可高估也不该低估他。
        active.AddRange(usage.Select(x =>
            new ActiveSpan(x.StartAt, x.StartAt.AddSeconds(x.DurationSeconds))));

        return ProfileCalculator.Build(samples, days, active);
    }

    /// <summary>
    /// 一条任务算哪一天。
    ///
    /// 【为什么优先用计划时间，而不是完成时间】
    /// 画像问的是"安排在这一天的事，有没有在这一天动起来"。
    /// 用完成时间的话，一件拖延到第二天才做完的事会被算进第二天，
    /// 那"这一天完成率"反而变好看了——拖过去的事被记成了别人的功劳。
    /// </summary>
    private static DateOnly DayOf(Item x)
    {
        var moment = x.StartAt ?? x.CompletedAt ?? x.CreatedAt;
        return DayBoundary.Resolve(moment);
    }
}
