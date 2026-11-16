namespace DayManager.Core.Profile;

/// <summary>
/// 一条任务的实测样本。
///
/// 【为什么不直接把 Item 实体传进画像计算】
/// 画像是一个纯函数：给它一堆数据，它给回一堆指标。
/// 传实体的话，测试就得先造出完整实体（连带十几个无关字段），
/// 而画像真正关心的只有这五个数。用它当传参的窄接口，
/// 测试和调用方都只需要准备真正用到的东西。
///
/// 【为什么"跳过"也在这里】
/// 没做的事比做完的事信息量更大——它直接说明是什么把他挡住了。
/// 只看完成率的话，永远不知道该改什么。
/// </summary>
/// <param name="PlannedStart">计划开始时刻（日程上写的几点）。空表示没定时间。</param>
/// <param name="ActualStart">真正按下"启动"的时刻。空表示没计过时。</param>
/// <param name="EstimateMinutes">事先估的时长。</param>
/// <param name="ActualMinutes">实际花了多久。</param>
/// <param name="CompletedAt">完成时刻。空表示还没做完。</param>
/// <param name="Skipped">是不是被跳过了。</param>
/// <param name="SkipReason">跳过时填的原因。</param>
public sealed record WorkSample(
    DateTimeOffset? PlannedStart,
    DateTimeOffset? ActualStart,
    int? EstimateMinutes,
    int? ActualMinutes,
    DateTimeOffset? CompletedAt,
    bool Skipped,
    string? SkipReason);

/// <summary>一天的完成率原料。</summary>
public sealed record DayRate(DateOnly Day, int Planned, int Completed);

/// <summary>一段"他在做事"的时间（任务计时或屏幕前的非娱乐时间）。</summary>
public sealed record ActiveSpan(DateTimeOffset Start, DateTimeOffset End);

/// <summary>
/// 日结行：某一天已经固化下来的客观量。
///
/// 【为什么趋势要读它，而不是重新去扫明细】
/// 日结存的是"那天当时的样子"。事后再从明细算的话，
/// 一条任务被改期、被删除，历史那天的数字就跟着变了——
/// 趋势线会自己动，那就没有"看到自己的变化"这回事了。
/// </summary>
public sealed record DailyRollupRow(
    DateOnly Day,
    int Planned,
    int Completed,
    int? AverageStartDelayMinutes,
    int FocusMinutes);

/// <summary>
/// 启动延迟：从"计划几点做"到"真的开始动"隔了多久。
///
/// 【为什么要同时给平均数和中位数】
/// 平均数会被一次极端的拖延带偏（比如某次隔了五小时）。
/// 中位数说的是"通常"，平均数说的是"总体代价"。
/// 给 AI 的时候用中位数更稳，但平均数能反映真实损失的时间。
/// </summary>
public sealed record StartDelayStat(int SampleCount, int AverageMinutes, int MedianMinutes);

/// <summary>
/// 估时偏差：实际 ÷ 预估。1.0 是估得刚好，1.6 是实际比估的多六成。
///
/// 【为什么存比值而不是"平均差多少分钟"]]
/// 一件 30 分钟的事估成 20 分钟，和一件 3 小时的事估成 2 小时，
/// 都差 10 分钟和 1 小时，但严重程度完全不同。比值才能跨任务比较。
/// </summary>
public sealed record EstimationStat(int SampleCount, double ActualOverEstimate);

/// <summary>某个小时里累计做了多久事。用来找"哪个时段他真的在动"。</summary>
public sealed record HourBucket(int Hour, int Minutes);

/// <summary>没做成的原因及次数。</summary>
public sealed record SkipReasonCount(string Reason, int Count);

/// <summary>
/// 一周（或某个周期）的汇总，用于趋势对比。
///
/// 【为什么周期是一周而不是一天】
/// 单天的数字噪声太大——一天没做不代表什么，连续一周才有意义。
/// 而按月又太粗，一个月看不出变化。周刚好是"能看出变化又不至于抖动"的粒度。
/// </summary>
public sealed record PeriodStat(
    DateOnly PeriodStart,
    int SampleDays,
    int? AverageStartDelayMinutes,
    double CompletionRate,
    int ActiveMinutes,
    int Planned,
    int Completed);

/// <summary>
/// 画像：从行为里算出来的事实集合。
///
/// 【为什么每一项都可空】
/// 空的意思是"数据还不够，别下结论"。这比返回 0 或返回一句"你没有拖延"
/// 安全得多——AI 拿到 null 就自然跳过，拿到 0 会说出"你从不需要热身"这种话。
///
/// 【为什么明明可空，还要单独记原始样本数】
/// 可空只说明"还不够"，没说明"还差多少"。
/// 而给他自己看的那一版必须能说出"再记 4 次就有结论"——
/// 否则那一块永远是一句"数据不足"，等于没做。样本数是用来算这个差的。
///
/// 【为什么不存"他是什么样的人"这种字段】
/// 那是结论，而结论会过期、会固化。这里只放事实，
/// 结论每次现算，旧数据自然被新数据推翻。
/// </summary>
public sealed record UserProfile(
    int SampleDays,
    StartDelayStat? StartDelay,
    EstimationStat? Estimation,
    IReadOnlyList<HourBucket> ActiveHours,
    IReadOnlyList<SkipReasonCount> SkipReasons,
    IReadOnlyList<DayRate> RecentDays,
    int StartDelaySamples = 0,
    int EstimationSamples = 0,
    int SkipSamples = 0)
{
    /// <summary>一点画像都没攒够时的空值。</summary>
    public static UserProfile Empty { get; } = new(
        0, null, null,
        Array.Empty<HourBucket>(),
        Array.Empty<SkipReasonCount>(),
        Array.Empty<DayRate>());
}

/// <summary>
/// 趋势对比：两个周期之间某个指标的变化。
///
/// 【为什么带 <paramref name="Improved"/>】
/// 展示的时候要靠它挑"该先说哪条"。这个软件是对抗逃避的，
/// 先说进步是有治疗作用的；退步不是不能显示，但不该排在最前面、
/// 也不该做成排行榜——那跟这个软件的目的相反。
/// </summary>
public sealed record TrendItem(
    string Label,
    string BeforeText,
    string AfterText,
    bool Improved,
    bool EnoughData,
    string ChangeWord);
