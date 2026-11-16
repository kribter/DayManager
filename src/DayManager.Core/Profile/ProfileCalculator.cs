namespace DayManager.Core.Profile;

/// <summary>
/// 画像的计算。全部是纯函数：进数据，出指标，不碰数据库、不读时钟。
///
/// 【为什么必须是纯函数】
/// 画像是要喂给 AI 的判断依据。它算错了，AI 会一路错下去，
/// 而且错得很自然——"你启动平均要 20 分钟"这种话听起来完全合理，
/// 没人会去怀疑它。这种错误只能靠测试发现，所以每一步都得能被断言。
///
/// 【为什么"样本不足就返回 null"这条要写死在这里】
/// 三天的行为说明不了任何事，但 AI 拿到"平均启动 8 分钟"就会当成事实去用，
/// 说出来的话会变成"你总是……"。null 让它没得说，比让它说错安全。
/// </summary>
public static class ProfileCalculator
{
    /// <summary>
    /// 出结论至少要几个样本。
    ///
    /// 【为什么是 5】
    /// 3 个太少（一次异常就能带偏中位数），10 个又太慢——
    /// 按一天一两件算，要攒半个月才有第一条结论，用户会觉得它反应迟钝。
    /// 5 是个"快但不至于胡说"的折中。
    /// </summary>
    public const int MinSample = 5;

    /// <summary>
    /// 跳过原因的门槛比别的小。
    ///
    /// 【为什么单独设一个更小的值】
    /// 跳过本来就是稀疏事件（不像每天都有任务），
    /// 用 5 的话几乎永远攒不出结论，那这个最有信息量的指标就废了。
    /// 3 次就能看出"他总栽在同一类事上"。
    /// </summary>
    public const int MinSkipSample = 3;

    /// <summary>累计做事不到这么多分钟，就不谈"哪个时段他在动"。</summary>
    public const int MinActiveMinutes = 120;

    /// <summary>最近趋势看多少天。超过这个天数的旧数据不再进"当前结论"。</summary>
    public const int RecentDays = 14;

    /// <summary>一天最多 24 小时，超过这个数的"延迟"一定是数据错了，不算。</summary>
    private const int MaxPlausibleDelayMinutes = 24 * 60;

    /// <summary>
    /// 从原始样本里算出画像。
    /// </summary>
    /// <param name="samples">任务样本（启动、估时、实际、跳过）。</param>
    /// <param name="days">每天的完成情况。</param>
    /// <param name="activeSpans">他在做事的时间段。</param>
    /// <param name="minSample">出结论的最小样本数，一般用默认值。</param>
    public static UserProfile Build(
        IReadOnlyList<WorkSample> samples,
        IReadOnlyList<DayRate> days,
        IReadOnlyList<ActiveSpan> activeSpans,
        int minSample = MinSample)
    {
        if (samples.Count == 0 && days.Count == 0 && activeSpans.Count == 0)
        {
            return UserProfile.Empty;
        }

        // 三个"按次数攒"的指标各要同时拿到结论和样本数：
        // 结论给 AI（够门槛才给），样本数给他自己看（用来算"还差几次"）。
        var startDelay = BuildStartDelay(samples, minSample);
        var estimation = BuildEstimation(samples, minSample);
        var skipReasons = BuildSkipReasons(samples);

        return new UserProfile(
            SampleDays: days.Count,
            StartDelay: startDelay.Stat,
            Estimation: estimation.Stat,
            ActiveHours: BuildActiveHours(activeSpans),
            SkipReasons: skipReasons.Reasons,
            RecentDays: days
                .OrderByDescending(x => x.Day)
                .Take(RecentDays)
                .ToList(),
            StartDelaySamples: startDelay.SampleCount,
            EstimationSamples: estimation.SampleCount,
            SkipSamples: skipReasons.Total);
    }

    /// <summary>启动延迟。只统计"定了时间、也真的动了"的那些次。</summary>
    private static (StartDelayStat? Stat, int SampleCount) BuildStartDelay(
        IReadOnlyList<WorkSample> samples,
        int minSample)
    {
        var delays = new List<int>();

        foreach (var s in samples)
        {
            // 没定时间的（PlannedStart 空）谈不上"延迟"——那是想起来才做的事。
            // 没按启动的（ActualStart 空）没法知道他到底什么时候动的手。
            if (s.PlannedStart is null || s.ActualStart is null)
            {
                continue;
            }

            var minutes = (int)(s.ActualStart.Value - s.PlannedStart.Value).TotalMinutes;

            // 提前开始（负数）不进延迟统计：那不是拖延，是另一种好事。
            // 大到离谱的说明这条数据本身有问题（比如改过时间），也不进。
            if (minutes < 0 || minutes > MaxPlausibleDelayMinutes)
            {
                continue;
            }

            delays.Add(minutes);
        }

        if (delays.Count < minSample)
        {
            return (null, delays.Count);
        }

        var ordered = delays.OrderBy(x => x).ToList();

        return (
            new StartDelayStat(
                SampleCount: ordered.Count,
                AverageMinutes: (int)ordered.Average(),
                MedianMinutes: ordered[ordered.Count / 2]),
            ordered.Count);
    }

    /// <summary>估时偏差。要同时有预估和实际两个数才有意义。</summary>
    private static (EstimationStat? Stat, int SampleCount) BuildEstimation(
        IReadOnlyList<WorkSample> samples,
        int minSample)
    {
        var ratios = new List<double>();

        foreach (var s in samples)
        {
            if (s.EstimateMinutes is not > 0 || s.ActualMinutes is not > 0)
            {
                continue;
            }

            ratios.Add((double)s.ActualMinutes.Value / s.EstimateMinutes.Value);
        }

        if (ratios.Count < minSample)
        {
            return (null, ratios.Count);
        }

        // 用中位数而不是平均数：一次极端（估 30 分钟做了 5 小时）
        // 不该把整条结论拽走。
        return (new EstimationStat(ratios.Count, Median(ratios)), ratios.Count);
    }

    /// <summary>
    /// 哪个小时他在动。
    ///
    /// 【为什么要按小时拆开，而不是直接加总】
    /// 一段 13:30–15:10 的做事时间，不能整段记在 13 点上——
    /// 那样"13 点"会被算成 100 分钟，而 14 点变成 0。
    /// 跨小时的部分得劈开算到各自头上。
    /// </summary>
    private static IReadOnlyList<HourBucket> BuildActiveHours(
        IReadOnlyList<ActiveSpan> activeSpans)
    {
        var byHour = new Dictionary<int, double>();
        var total = 0.0;

        foreach (var span in activeSpans)
        {
            if (span.End <= span.Start)
            {
                continue;
            }

            var cursor = span.Start.LocalDateTime;
            var end = span.End.LocalDateTime;

            while (cursor < end)
            {
                var hourEnd = cursor.Date.AddHours(cursor.Hour + 1);
                var stop = hourEnd < end ? hourEnd : end;
                var minutes = (stop - cursor).TotalMinutes;

                if (minutes > 0)
                {
                    var hour = cursor.Hour;
                    byHour[hour] = byHour.TryGetValue(hour, out var had) ? had + minutes : minutes;
                    total += minutes;
                }

                cursor = stop;
            }
        }

        if (total < MinActiveMinutes)
        {
            return Array.Empty<HourBucket>();
        }

        return byHour
            .Select(x => new HourBucket(x.Key, (int)x.Value))
            .OrderByDescending(x => x.Minutes)
            .Take(4)
            .ToList();
    }

    /// <summary>
    /// 没做成的原因。
    ///
    /// 【为什么"没说原因"也要单独算一类】
    /// 跳过时没填原因的人占多数。把它们丢掉的话，剩下的样本永远凑不够；
    /// 而"他不爱填原因"本身也是一条信息——说明这个提示对他没用，该换个问法。
    /// </summary>
    private static (IReadOnlyList<SkipReasonCount> Reasons, int Total) BuildSkipReasons(
        IReadOnlyList<WorkSample> samples)
    {
        var groups = samples
            .Where(x => x.Skipped)
            .GroupBy(x => string.IsNullOrWhiteSpace(x.SkipReason)
                ? "没说原因"
                : x.SkipReason!.Trim())
            .Select(g => new SkipReasonCount(g.Key, g.Count()))
            .OrderByDescending(x => x.Count)
            .ToList();

        var total = groups.Sum(x => x.Count);

        return total < MinSkipSample
            ? (Array.Empty<SkipReasonCount>(), total)
            : (groups.Take(3).ToList(), total);
    }

    private static double Median(List<double> values)
    {
        var ordered = values.OrderBy(x => x).ToList();
        var mid = ordered.Count / 2;

        // 偶数个取中间两个的平均，避免总偏向一侧。
        return ordered.Count % 2 == 0
            ? (ordered[mid - 1] + ordered[mid]) / 2.0
            : ordered[mid];
    }
}
