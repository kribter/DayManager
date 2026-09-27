using DayManager.Core.Enums;

namespace DayManager.Core.Entities;

/// <summary>
/// 某一天的上下文与总结。
///
/// 【为什么单独建这张表，而不是每次都从明细里现算】
/// 完成率、专注时长这些指标理论上都能从原始记录实时算出来，
/// 但那意味着每次打开回顾页面都要扫描几万条明细记录，手机上会很卡。
/// 更重要的是，这张表存的是"当天写下的心情和复盘"——这是原始数据里没有的东西。
/// 让它兼当每日快照，打开回顾界面就是一条记录的查询。
/// </summary>
public class DailyContext : SyncEntityBase
{
    /// <summary>
    /// 日期。
    ///
    /// 【为什么用 DateOnly 而不是 DateTime】
    /// DateOnly 是 .NET 6 引入的类型，专门表示"某一天"。
    /// 用 DateTime 存的话，同一天会因为时分秒不同变成多条不同记录，
    /// 判断"是不是同一天"还得写 .Date 转换，一不小心就出 bug。
    /// </summary>
    public DateOnly Date { get; set; }

    /// <summary>
    /// 这一天属于什么类型。节假日类型直接决定干预引擎的松紧程度。
    /// </summary>
    public DayType DayType { get; set; } = DayType.Workday;

    /// <summary>
    /// 是否节假日 / 休息日。你认为在这种日子里不该对自己太苛刻，
    /// 所以这是个干预引擎要读的关键开关。
    /// </summary>
    public bool IsHoliday { get; set; }

    /// <summary>
    /// 手动写的复盘或心情。你说的"这一天做了什么、又有多少没做"的主观部分就存在这里，
    /// 它和下面的自动统计互补——数据是客观的，但你的感受只有你自己知道。
    /// </summary>
    public string? UserNote { get; set; }

    /// <summary>
    /// 心情评分（1-5）。打分比写文字成本低，
    /// 攒久了能看出"完成率低的日子心情也差"这类相关性。可空，不想填就不用填。
    /// </summary>
    public int? MoodScore { get; set; }

    /// <summary>
    /// AI 生成的当日总结，控制在 60-80 字。
    /// 存下来是为了让明天的 AI 能看到昨天说了什么，保持对话连贯。
    /// </summary>
    public string? AiDailySummary { get; set; }

    // ---------- 当日统计快照 ----------

    /// <summary>当天计划的条目数。</summary>
    public int PlannedCount { get; set; }

    /// <summary>当天完成的条目数。</summary>
    public int CompletedCount { get; set; }

    /// <summary>当天放弃 / 取消的条目数。</summary>
    public int CancelledCount { get; set; }

    /// <summary>
    /// 当天平均启动延迟（分钟）。
    ///
    /// 【为什么这个指标值得单独存】
    /// 完成率是结果，启动延迟才是你这个具体问题（知道该做但启动不了）的过程指标。
    /// 把它按天固化下来，才能画出"启动越来越快"的趋势线。
    /// </summary>
    public int? AverageStartDelayMinutes { get; set; }

    /// <summary>当天专注类应用的合计时长（分钟）。</summary>
    public int FocusMinutes { get; set; }

    /// <summary>当天娱乐类应用的合计时长（分钟）。</summary>
    public int EntertainmentMinutes { get; set; }
}
