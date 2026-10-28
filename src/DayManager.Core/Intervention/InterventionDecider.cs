using DayManager.Core.Enums;
using DayManager.Core.Planning;

namespace DayManager.Core.Intervention;

/// <summary>
/// 为什么这次不提醒。**"不提"也是一种决定，而且必须说得清是哪一条挡住的。**
///
/// 【为什么要把原因枚举出来，而不是返回 null 了事】
/// 判断"没弹提醒"的原因只有五种，而它们在界面上长得完全一样：什么都没有。
/// 排查时最容易发生的对话是"我以为它拦了，其实阈值没到"或者"我明明关掉了它还在弹"。
/// 有了这个枚举，日志里能写清是哪一条，冒烟测试也能对每一种分别断言。
/// </summary>
public enum InterventionSkipReason
{
    /// <summary>该提醒。</summary>
    None = 0,

    /// <summary>总开关关着。</summary>
    Disabled = 1,

    /// <summary>当前这个应用不算娱乐。</summary>
    NotEntertainment = 2,

    /// <summary>今天已经按过「别再提了」。</summary>
    Snoozed = 3,

    /// <summary>还没到阈值。</summary>
    BelowThreshold = 4,

    /// <summary>刚到过，冷却时间里。</summary>
    CoolingDown = 5
}

/// <summary>一条要送出去的提醒。</summary>
/// <param name="Title">通知标题，一行。</param>
/// <param name="Body">正文。必须包含一个具体的下一步动作或者一句明确的许可。</param>
public sealed record InterventionNotice(string AppId, string Title, string Body);

/// <summary>判定的全部输入。做成一个对象，是为了不让调用方和判定器在参数顺序上出错。</summary>
/// <param name="Kind">用户在设置里给这个应用的归类；Neutral 时交给内置名单猜。</param>
/// <param name="ContinuousMinutes">**这一段**连续用了多久。不是当天累计——理由见 <see cref="InterventionDecider"/> 的类注释。</param>
/// <param name="SnoozedOn">用户在哪一天按过「今天别再提了」。</param>
/// <param name="Today">今天（本地日期）。和 SnoozedOn 比较用。</param>
/// <param name="RemainingCount">今天还没做完的件数（跳过的不算）。</param>
/// <param name="FirstRemainingTitle">其中第一件的标题。文案里点名它，比"你还有任务"有用得多。</param>
public sealed record InterventionContext(
    string AppId,
    string AppName,
    int ContinuousMinutes,
    AppKind Kind,
    bool Enabled,
    int ThresholdMinutes,
    int CooldownMinutes,
    DateTimeOffset? LastNotifiedAt,
    DateOnly? SnoozedOn,
    DateOnly Today,
    int RemainingCount,
    string? FirstRemainingTitle,
    DateTimeOffset Now);

/// <summary>
/// 判断"现在该不该提醒，说什么"。
///
/// 【为什么这整个东西是一个纯函数，而不是塞进某个服务里】
/// 它要同时满足两件互相拉扯的事：拦得住逃避，又不能变成骚扰工具。
/// 这两件事的边界条件（阈值、冷却、赦免、有没有未完成的事）组合起来有十几种情形，
/// 而"多弹了一次"的代价是用户把功能关掉——这种地方必须能逐条验证，
/// 不能靠"打开 B 站看它弹不弹"来试。
/// 纯函数意味着：同样的输入永远同样的输出，冒烟测试可以断言每一种组合。
///
/// 【为什么只看"这一段连续多久"，不看"今天累计多久"】
/// 累计是统计，连续是行为。一天里分三次各看 10 分钟 B 站，和一口气看 30 分钟，
/// 是两种完全不同的状态；真正需要被拦的是后者。
/// 用累计判定的话，早上看了 25 分钟，晚上一打开就立刻被提醒——
/// 那时人才刚坐下，提醒毫无道理，只会让人烦。
///
/// 【为什么"没有未完成的事"时也要说话】
/// 需求里写的是"双向"：不只在逃避时提醒，也在该休息时给出许可。
/// 一个只会催的工具会被关掉；一个在该夸的时候夸的工具才会被留着。
/// 而且这里的正反馈是真的——今天的清单确实清空了，不是"你真棒"这种空话。
/// </summary>
public static class InterventionDecider
{
    /// <summary>
    /// 决定这一次要不要提醒。
    /// </summary>
    public static InterventionDecision Decide(InterventionContext c)
    {
        if (!c.Enabled)
        {
            return new InterventionDecision(null, InterventionSkipReason.Disabled);
        }

        if (!IsEntertainment(c))
        {
            return new InterventionDecision(null, InterventionSkipReason.NotEntertainment);
        }

        // 赦免优先于一切：用户已经明说"今天别再说了"，那就今天都不说。
        // 顺序很重要——放在阈值之后的话，会出现"虽然按了别再提，但时间到了又弹"这种言而无信。
        if (c.SnoozedOn == c.Today)
        {
            return new InterventionDecision(null, InterventionSkipReason.Snoozed);
        }

        // 阈值配成 0 或负数时不触发，而不是"立刻触发"。
        // 配错一个数字的后果必须是"功能不工作"，不能是"疯狂弹窗"。
        if (c.ThresholdMinutes <= 0 || c.ContinuousMinutes < c.ThresholdMinutes)
        {
            return new InterventionDecision(null, InterventionSkipReason.BelowThreshold);
        }

        if (c.LastNotifiedAt is not null &&
            c.Now - c.LastNotifiedAt.Value < TimeSpan.FromMinutes(Math.Max(0, c.CooldownMinutes)))
        {
            return new InterventionDecision(null, InterventionSkipReason.CoolingDown);
        }

        return new InterventionDecision(BuildNotice(c), InterventionSkipReason.None);
    }

    /// <summary>
    /// 这个应用算不算娱乐：用户标过的以用户为准，没标过才问名单。
    ///
    /// 【为什么这里不自己写 switch】
    /// 时间页上那个徽章用的是同一个判断（<see cref="EntertainmentApps.Classify"/>）。
    /// 两处各写一份的话，"时间页说娱乐、这里说不娱乐"这种矛盾不会报错，
    /// 只能靠人拿两个界面对比才发现。
    /// </summary>
    private static bool IsEntertainment(InterventionContext c) =>
        EntertainmentApps.Classify(c.AppId, c.Kind) == AppKind.Entertainment;

    private static InterventionNotice BuildNotice(InterventionContext c)
    {
        var name = string.IsNullOrWhiteSpace(c.AppName) ? c.AppId : c.AppName;
        var spent = DurationText.Format(c.ContinuousMinutes);

        if (c.RemainingCount <= 0)
        {
            // 无事可做的时候，提醒的内容是"许可"。标题不写应用名，
            // 因为这一刻的重点不是"你在看什么"，而是"今天已经够了"。
            return new InterventionNotice(
                c.AppId,
                "今天的事都做完了",
                $"{name} 随便看——这是你自己挣来的。");
        }

        // 【为什么正文里要点名具体那一件，还要带"就 10 分钟"】
        // 你之前说过：卡住的地方不是忘了要做，是启动不了。
        // 所以提醒不能只说"你还有 3 件事"，那只是把焦虑重新推一遍；
        // 要给出一个**小到不需要下决心**的入口（10 分钟），
        // 而"10 分钟"恰好也是这个软件想让用户学会的东西：真正难的是开始。
        var first = Shorten(c.FirstRemainingTitle, 14);

        var body = first is null
            ? $"还有 {c.RemainingCount} 件没做完。挑一件，就 10 分钟。"
            : $"还有 {c.RemainingCount} 件没做完。先做「{first}」，就 10 分钟。";

        return new InterventionNotice(c.AppId, $"{name} 已经 {spent}", body);
    }

    /// <summary>
    /// 标题太长会把通知挤成两行甚至三行，这里截一下。
    /// 用"…"结尾而不是硬切，让人知道还有下文。
    /// </summary>
    private static string? Shorten(string? text, int max)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var trimmed = text.Trim();
        return trimmed.Length <= max ? trimmed : trimmed[..max] + "…";
    }
}

/// <summary>判定结果：提醒内容（可能为 null）+ 为什么。</summary>
public sealed record InterventionDecision(InterventionNotice? Notice, InterventionSkipReason Reason)
{
    public bool ShouldNotify => Notice is not null;
}
