using DayManager.Core.Enums;

namespace DayManager.Core.Planning;

/// <summary>
/// AI 拆解出来的一个子任务（还没定哪天做）。
/// </summary>
/// <param name="Title">任务标题。</param>
/// <param name="EstimateMinutes">预计耗时（分钟）。</param>
/// <param name="MinimalStep">最小启动步骤：两分钟内能做完的第一步。</param>
/// <param name="Kind">该做的还是想做的。</param>
public sealed record PlannedTask(
    string Title,
    int EstimateMinutes,
    string? MinimalStep,
    ItemKind Kind = ItemKind.MustDo);

/// <summary>
/// 已经落到某一天的子任务。
/// </summary>
public sealed record ScheduledTask(PlannedTask Task, DateOnly Date);

/// <summary>
/// 摊分结果。
/// </summary>
/// <param name="Scheduled">排上了的任务。</param>
/// <param name="Overflow">按给定的每天额度排不下的任务。</param>
/// <param name="RequiredDays">排完这些事实际需要几天。</param>
/// <param name="AvailableDays">从开始日到截止日一共有几天。</param>
/// <param name="TotalMinutes">总预计耗时。</param>
/// <param name="DailyMinutes">使用的每日额度。</param>
public sealed class SpreadingResult
{
    public IReadOnlyList<ScheduledTask> Scheduled { get; init; } = Array.Empty<ScheduledTask>();
    public IReadOnlyList<PlannedTask> Overflow { get; init; } = Array.Empty<PlannedTask>();

    public int RequiredDays { get; init; }
    public int AvailableDays { get; init; }
    public int TotalMinutes { get; init; }
    public int DailyMinutes { get; init; }

    /// <summary>每一天的实际装载量（分钟）。</summary>
    public IReadOnlyDictionary<DateOnly, int> DayLoads { get; init; } = new Dictionary<DateOnly, int>();

    /// <summary>排得下 = 没有溢出。</summary>
    public bool Fits => Overflow.Count == 0;

    /// <summary>
    /// 有几天超过了每天额度。
    ///
    /// 【为什么"排下了"和"没超预算"是两件事，必须分开说】
    /// 一条任务只要本身比一天的额度还大，它就会被单独放进某一天
    /// （不这么办的代价更大：它永远排不进去，或者被切碎到两天——
    /// 跨天做的任务要求用户记住"昨天做到哪了"，那本身就是启动摩擦）。
    /// 于是"全部排进去了"与"每天都不超"并不等价。
    /// 只报前者，用户看到的就会是"12 条都排好了"
    /// 而实际画面上每天写着 300 分钟——这就是"说好的 120 分钟呢"的来源。
    /// </summary>
    public int OverBudgetDays => DayLoads.Count(x => x.Value > DailyMinutes);

    /// <summary>最重的那一天装了多少分钟。</summary>
    public int MaxDayMinutes => DayLoads.Count == 0 ? 0 : DayLoads.Values.Max();

    /// <summary>
    /// 给 AI 或界面用的一句话说明。
    ///
    /// 【为什么排不下时要说清楚差多少】
    /// 只说"排不下"没有任何行动价值。要给出"需要 N 天、只有 M 天、
    /// 每天 X 分钟"这三个数，用户才能做决策：是延长截止日，还是砍掉几条，
    /// 还是接受每天多做一些。这是文档里 C2「重新协商」的入口。
    /// </summary>
    public string Describe()
    {
        // 超预算的说明要放在最前面：它是"这份计划能不能照着做"的第一信息，
        // 比"排了几条"重要。平均每天多少分钟是好看的数字，
        // 但平均值的用处恰恰在于掩盖极值——所以必须先说极值。
        var overNote = OverBudgetDays > 0
            ? $"注意：有 {OverBudgetDays} 天超过了你设定的每天 {DailyMinutes} 分钟"
              + $"（最多的一天 {MaxDayMinutes} 分钟）——其中有些任务单条就超过一天的额度，"
              + "说明拆得还不够细。"
            : string.Empty;

        if (Fits)
        {
            return (overNote.Length == 0 ? string.Empty : overNote + " ")
                 + $"共 {Scheduled.Count} 条、约 {TotalMinutes} 分钟，摊在 {RequiredDays} 天里，"
                 + $"平均每天 {Math.Round((double)TotalMinutes / Math.Max(RequiredDays, 1))} 分钟。";
        }

        var overflowMinutes = Overflow.Sum(x => x.EstimateMinutes);
        return (overNote.Length == 0 ? string.Empty : overNote + " 另外，")
             + $"按每天 {DailyMinutes} 分钟的保守额度排不完：截止日之前只有 {AvailableDays} 天，"
             + $"剩下的 {Overflow.Count} 条（约 {overflowMinutes} 分钟）放不进去。"
             + $"要么延长截止日，要么砍掉一部分，要么把每日额度调高。";
    }
}
