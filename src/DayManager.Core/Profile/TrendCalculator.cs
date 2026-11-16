using DayManager.Core.Planning;

namespace DayManager.Core.Profile;

/// <summary>
/// 趋势：把两个周期的画像放在一起比，得出"他在变好还是变差"。
///
/// 【为什么这是单独一层，而不是画像的一部分】
/// 画像回答"他现在是什么样"，趋势回答"他变成什么样了"。
/// 前者会过期（所以要现算、只看最近），后者必须永久留着——
/// 三个月前的自己和现在的自己之间的那条线，是这个软件最有力的正反馈：
/// "你启动一件事故要 40 分钟，现在 15 分钟"，比它夸一百句都管用。
///
/// 【为什么拿中位数比，不拿平均数比】
/// 平均数会被一次极端拖走，两个周期一比，可能只是那一次不一样。
/// 中位数说的是"通常"，比较稳。
/// </summary>
public static class TrendCalculator
{
    /// <summary>两个周期之间至少要各有多少天，才敢说"变了"。</summary>
    public const int MinDaysPerPeriod = 3;

    /// <summary>
    /// 每个周期多长。七天一比：按月太慢（一个月看不出变化），按天太抖。
    ///
    /// 【为什么这个常量放在 Core 而不是各自的服务里】
    /// 后端喂给 AI 的趋势、时间页上给他自己看的趋势，必须是同一个周期，
    /// 否则会出现"它说这周变好了，页面上写着变差了"。
    /// </summary>
    public const int PeriodDays = 7;

    /// <summary>
    /// 把一个周期内的日结行汇总成一个可对比的快照。
    /// </summary>
    /// <param name="periodStart">这个周期的第一天，只用作标签。</param>
    /// <param name="rows">这个周期内已经结算过的天。</param>
    public static PeriodStat BuildPeriod(
        DateOnly periodStart,
        IReadOnlyList<DailyRollupRow> rows)
    {
        var planned = rows.Sum(x => x.Planned);
        var completed = rows.Sum(x => x.Completed);

        // 每天的平均延迟再平均一次。严格说该按当天的样本数加权，
        // 但日结只存了日均值（明细固然更准，代价是每天都要扫一遍库），
        // 而趋势看的是"往哪走"，不是精确到分钟——这个精度够了。
        var delays = rows
            .Where(x => x.AverageStartDelayMinutes is not null)
            .Select(x => (double)x.AverageStartDelayMinutes!.Value)
            .ToList();

        return new PeriodStat(
            PeriodStart: periodStart,
            SampleDays: rows.Count(x => x.Planned > 0),
            AverageStartDelayMinutes: delays.Count == 0 ? null : (int)delays.Average(),
            CompletionRate: planned == 0 ? 0 : (double)completed / planned,
            ActiveMinutes: rows.Sum(x => x.FocusMinutes),
            Planned: planned,
            Completed: completed);
    }

    /// <summary>
    /// 对比两个周期，返回三条：启动、完成率、做事时长。
    ///
    /// 【为什么 Improved 由"变好"定义，而不是由"变大"定义】
    /// 启动延迟变小是好的，完成率变大是好的，做事时长变大是好的——
    /// 三个指标的方向不一样，写死在这里，调用方就不会搞反。
    /// </summary>
    public static IReadOnlyList<TrendItem> Compare(PeriodStat? before, PeriodStat? after)
    {
        if (before is null || after is null)
        {
            return Array.Empty<TrendItem>();
        }

        // 天数不够的时候只说"还没攒够"，不给数字——
        // 三天对三天得出的"你进步了"是假的，而且假得很像真的。
        var enough = before.SampleDays >= MinDaysPerPeriod
                     && after.SampleDays >= MinDaysPerPeriod;

        var items = new List<TrendItem>
        {
            BuildDelay(before, after, enough),
            BuildRate(before, after, enough),
            BuildActive(before, after, enough)
        };

        return items;
    }

    private static TrendItem BuildDelay(PeriodStat before, PeriodStat after, bool enough)
    {
        var a = before.AverageStartDelayMinutes;
        var b = after.AverageStartDelayMinutes;
        var improved = a is not null && b is not null && b.Value < a.Value;

        return new TrendItem(
            Label: "启动要多久",
            BeforeText: a is null ? "还没有数据" : DurationText.About(a.Value),
            AfterText: b is null ? "还没有数据" : DurationText.About(b.Value),
            Improved: improved,
            EnoughData: enough && a is not null && b is not null,
            ChangeWord: improved ? "快了" : "慢了");
    }

    private static TrendItem BuildRate(PeriodStat before, PeriodStat after, bool enough)
    {
        var improved = after.CompletionRate > before.CompletionRate;

        return new TrendItem(
            Label: "完成率",
            BeforeText: $"{before.Planned} 件里做了 {before.Completed} 件",
            AfterText: $"{after.Planned} 件里做了 {after.Completed} 件",
            Improved: improved,
            EnoughData: enough,
            ChangeWord: improved ? "多做了" : "少做了");
    }

    private static TrendItem BuildActive(PeriodStat before, PeriodStat after, bool enough)
    {
        var improved = after.ActiveMinutes > before.ActiveMinutes;

        return new TrendItem(
            Label: "做事时长",
            BeforeText: DurationText.About(before.ActiveMinutes),
            AfterText: DurationText.About(after.ActiveMinutes),
            Improved: improved,
            EnoughData: enough,
            ChangeWord: improved ? "多了" : "少了");
    }
}
