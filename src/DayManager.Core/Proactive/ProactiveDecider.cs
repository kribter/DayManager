namespace DayManager.Core.Proactive;

/// <summary>
/// AI 主动开口的四种场合。
///
/// 【为什么要区分场合，而不是统一成"AI 想说话了"】
/// 这四种话的**内容骨架完全不同**：早晨要告诉他今天有什么、建议先动哪一件；
/// 晚间只给一句 60-80 字的总结；日程前提醒必须点名那一条；
/// 连续低完成率那次要换语气。混成一种，模型只能靠猜语气，
/// 结果就是每次都说一堆正确的废话。
/// </summary>
public enum ProactiveKind
{
    /// <summary>没什么可说的。</summary>
    None = 0,

    /// <summary>早晨计划：今天排了什么、先动哪一件。</summary>
    Morning = 1,

    /// <summary>日程前提醒：某一条马上要开始了。</summary>
    Before = 2,

    /// <summary>晚间总结：正反馈 + 一个具体建议 + 一个数据。</summary>
    Evening = 3,

    /// <summary>连续几天完成率很低时的严肃谈话。</summary>
    LowRate = 4
}

/// <summary>
/// 为什么没开口。**和干预引擎一样，"不说"必须能说清是哪一条挡住的**——
/// 四种场合在界面上的表现完全一样（什么都没有），不写清楚就只能靠猜。
/// </summary>
public enum ProactiveSkipReason
{
    /// <summary>该开口。</summary>
    None = 0,

    /// <summary>总开关关着。</summary>
    Disabled = 1,

    /// <summary>距上次开口还在冷却时间里。</summary>
    CoolingDown = 2,

    /// <summary>该说的都说过了，或者还没到点儿。</summary>
    NothingToSay = 3
}

/// <summary>一条马上要开始的日程。</summary>
public sealed record ProactiveUpcoming(
    Guid ItemId,
    string Title,
    string? MinimalStep,
    DateTimeOffset StartAt);

/// <summary>
/// 某一天的完成情况。
///
/// 【为什么同时要 Total 和 Done，而不是直接传一个比率】
/// 空一天（一件都没排）不该被算成"完成率 0%"。
/// 那是休息，不是失败。判断需要看得见分母，光有比率没法区分这两种情况。
/// </summary>
public sealed record ProactiveDayRatio(DateOnly Day, int Total, int Done);

/// <summary>判定的全部输入。做成对象是为了不让调用方在参数顺序上出错。</summary>
public sealed record ProactiveContext
{
    /// <summary>现在。</summary>
    public required DateTimeOffset Now { get; init; }

    /// <summary>总开关。</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>今天已经开过口的场合。同一种一天只说一次。</summary>
    public IReadOnlySet<ProactiveKind> FiredToday { get; init; } = new HashSet<ProactiveKind>();

    /// <summary>上一次开口的时刻。用来做冷却，避免几种话挤在一起说。</summary>
    public DateTimeOffset? LastFiredAt { get; init; }

    /// <summary>还没提醒过的、即将开始的日程。</summary>
    public IReadOnlyList<ProactiveUpcoming> Upcoming { get; init; } = Array.Empty<ProactiveUpcoming>();

    /// <summary>
    /// 最近几天（不含今天）的完成情况，按日期升序。
    /// 判断"连续几天很低"只需要看前面这几天——今天还没过完，算它不公平。
    /// </summary>
    public IReadOnlyList<ProactiveDayRatio> RecentDays { get; init; } = Array.Empty<ProactiveDayRatio>();
}

/// <summary>判定结果。</summary>
/// <param name="Kind">要开口的场合。</param>
/// <param name="ItemId">场合为「日程前提醒」时，是那一条的 id；否则为空。</param>
/// <param name="Reason">没开口的原因。</param>
public sealed record ProactiveDecision(ProactiveKind Kind, Guid? ItemId, ProactiveSkipReason Reason)
{
    /// <summary>要不要开口。</summary>
    public bool ShouldSpeak => Kind != ProactiveKind.None;

    /// <summary>没开口。</summary>
    public static ProactiveDecision Skip(ProactiveSkipReason reason) =>
        new(ProactiveKind.None, null, reason);

    /// <summary>开口，且不是针对某一条。</summary>
    public static ProactiveDecision Speak(ProactiveKind kind) =>
        new(kind, null, ProactiveSkipReason.None);
}

/// <summary>
/// 判断"这一刻 AI 该不该主动开口"。纯函数，不碰数据库、不看系统时钟。
///
/// 【为什么判断要放在 Core 而不是客户端服务里】
/// 这是这个功能里唯一有资格被称为"逻辑"的部分：什么时候说、说几次、
/// 什么样算"连续很差"。放在客户端里就只能靠真机熬夜去试——
/// 早晨那条要等到早上八点才能验一次。做成纯函数，冒烟测试能把时钟拨到任意时刻，
/// 八点、二十一点、连着三天低完成率，几毫秒全验完。
/// </summary>
public static class ProactiveDecider
{
    /// <summary>早晨计划的窗口：[8:00, 11:00)。</summary>
    public const int MorningStartHour = 8;
    public const int MorningEndHour = 11;

    /// <summary>晚间总结从 21:00 开始。</summary>
    public const int EveningStartHour = 21;

    /// <summary>日程前提醒提前多少分钟。</summary>
    public const int BeforeMinutes = 10;

    /// <summary>
    /// 两次开口之间至少隔多久。
    ///
    /// 【为什么需要这条】
    /// 早晨计划刚说完，紧接着一条日程前提醒，再紧接着晚间总结——
    /// 每一条单看都对，挤在一起就成了骚扰。而"软件开始烦人"这件事，
    /// 用户只需要烦一次就会把它关掉。
    /// </summary>
    public const int CooldownMinutes = 60;

    /// <summary>连续多少天算"一直很低"。</summary>
    public const int LowRateDays = 3;

    /// <summary>完成率低于多少算很低。</summary>
    public const double LowRateThreshold = 0.3;

    /// <summary>
    /// 判断这一刻该说什么。
    ///
    /// 【为什么按这个顺序检查】
    /// 时效越强的越靠前：日程前提醒晚一分钟就过期了，早晨计划晚一小时也还成立。
    /// 一次只回一条——宁可少说一句，也不要一次弹三条。
    /// </summary>
    public static ProactiveDecision Decide(ProactiveContext ctx)
    {
        if (!ctx.Enabled)
        {
            return ProactiveDecision.Skip(ProactiveSkipReason.Disabled);
        }

        // 冷却：刚说过就别再说。
        //
        // 【为什么冷却不区分场合】
        // 区分了就等于允许"早晨刚说完、日程前提醒接着来"，
        // 而这正是最招人烦的那种连续弹出。一条冷却管住全部。
        if (ctx.LastFiredAt is { } last &&
            ctx.Now - last < TimeSpan.FromMinutes(CooldownMinutes))
        {
            return ProactiveDecision.Skip(ProactiveSkipReason.CoolingDown);
        }

        var fired = ctx.FiredToday;

        // ① 日程前提醒：有时效性，最先判。
        if (!fired.Contains(ProactiveKind.Before))
        {
            var due = ctx.Upcoming
                .Where(x => x.StartAt > ctx.Now
                            && x.StartAt - ctx.Now <= TimeSpan.FromMinutes(BeforeMinutes))
                .OrderBy(x => x.StartAt)
                .FirstOrDefault();

            if (due is not null)
            {
                return new ProactiveDecision(ProactiveKind.Before, due.ItemId, ProactiveSkipReason.None);
            }
        }

        var hour = ctx.Now.Hour;

        // ② 早晨计划。
        if (hour >= MorningStartHour && hour < MorningEndHour && !fired.Contains(ProactiveKind.Morning))
        {
            return ProactiveDecision.Speak(ProactiveKind.Morning);
        }

        // ③ 晚间总结。
        if (hour >= EveningStartHour && !fired.Contains(ProactiveKind.Evening))
        {
            return ProactiveDecision.Speak(ProactiveKind.Evening);
        }

        // ④ 连续几天完成率很低。
        if (!fired.Contains(ProactiveKind.LowRate) && IsConsistentlyLow(ctx.RecentDays))
        {
            return ProactiveDecision.Speak(ProactiveKind.LowRate);
        }

        return ProactiveDecision.Skip(ProactiveSkipReason.NothingToSay);
    }

    /// <summary>
    /// 最近是不是连着几天都很低。
    ///
    /// 【为什么空一天不算低】
    /// 一天没排任何事，完成率是 0（分母为零时按 0 算）。
    /// 要是把这种天算进"连续很差"，那周末休息两天之后，
    /// 周一一大早就会迎面撞上一句"你最近三天什么都没做成"——
    /// 它没错，但它会让人立刻把这个软件关掉。
    /// 所以要求：这几天每天都**确实排了事**，而且做出来的少于三成。
    /// </summary>
    public static bool IsConsistentlyLow(IReadOnlyList<ProactiveDayRatio> recentDays)
    {
        if (recentDays.Count < LowRateDays)
        {
            return false;
        }

        // 只看最后几天：传进来可能是一个月的量，判断"连续"只需要最近那几段。
        var window = recentDays.Skip(Math.Max(0, recentDays.Count - LowRateDays)).ToList();

        return window.All(d => d.Total > 0 && (double)d.Done / d.Total < LowRateThreshold);
    }
}
