using DayManager.Core.Entities;
using DayManager.Core.Enums;
using DayManager.Core.Planning;

namespace DayManager.Core.Intervention;

/// <summary>
/// 从今天还没做完的事里，挑出**这一刻适合被点名的那一件**。
///
/// 【为什么需要这么一个东西】
/// 2026-10-31 傍晚那次提醒长这样：
///     "Google Chrome 已经 15 分钟"
///     "还有 1 件没做完。先做「12点前躺下，手机放到够不着」，就 10 分钟。"
/// 荒谬的地方在于那一件是 23:59 才开始的事，而当时才 18:00。
/// 原因是当时直接取了清单里的第一条（<c>remaining.FirstOrDefault()</c>），零筛选——
/// 于是"文案里点名具体那一件"这个本来对的做法，点错了名反而比不点名更糟。
///
/// 【为什么宁可挑不出来，也不去猜任务标题】
/// "12点前躺下"这种措辞只有人（或者大模型）读得懂，本地猜不准：
/// 一条正则能挡住"XX 点前"，挡不住"睡前""下班后""等他回来再说"。
/// 猜错的代价是提醒继续说胡话，而"挑不出来"的代价只是文案退回不点名的那一句——
/// 后者明显更轻。所以这里只看**结构化时间**，不碰标题文本。
///
/// 【为什么它必须是纯函数】
/// 和 <see cref="InterventionDecider"/> 同一个理由：它要在冒烟测试里被逐条断言。
/// "该不该提醒"和"提醒里点谁的名"，两边都是几十字就能写错的地方，都得能单测。
/// </summary>
public static class NudgeTargetPicker
{
    /// <summary>
    /// 一件还没到点的事，在多近之内算"就是现在"。
    ///
    /// 【为什么留这 5 分钟而不是严格按时间点切】
    /// 排了 18:00 开始的事，17:58 被提醒去先做 10 分钟，不但不荒谬，
    /// 反而正好是"先把东西摆开"的时候——10 分钟本来就是热身的量。
    /// 反过来如果没有这个缓冲，17:58 这一分钟里刷手机就完全没人管了。
    /// </summary>
    public const int GraceMinutes = 5;

    /// <summary>
    /// 把"现在就能动手的事"按推荐顺序排出来：已经开了头的在前，其余保持原顺序。
    ///
    /// 【为什么要单独有个"清单"版本，而不是只有一个 Pick】
    /// 本地兜底只需要第一件（<see cref="Pick"/>）；
    /// 但把这个选择交给 AI 时，得让它看到**全部可选的那几件**再挑，
    /// 否则它只能在我替它挑好的那一件里打转，AI 的语义判断就白瞎了。
    /// 两处共用同一份筛选规则，不会出现"本地说不能点名、AI 却点出来了"。
    ///
    /// 【11-21 补：先过判成败，再过时间】
    /// 旧版只有 IsActionableNow，把「压根没排时间（StartAt 空）」一律当成
    /// 「纯待办、随时能做」，于是「0点上床睡觉」这种 StartAt 空 + DueAt=23:59
    /// 的作息约束被当成能动手的候选，22:44 说出「先睡 10 分钟」。
    /// 现在先读三根轴：守住类（VerdictMode.Never）、容器、定段常驻项一律不点名。
    /// 这不需要语义理解、不需要联网——离线兜底读一个字段就能判，
    /// 正是「分类落地最大的受益者是离线那条路」那句结论的兑现。
    /// </summary>
    public static IReadOnlyList<Item> Actionable(IReadOnlyList<Item> remaining, DateTimeOffset now)
    {
        if (remaining.Count == 0)
        {
            return Array.Empty<Item>();
        }

        var today = ItemPresence.LocalDay(now);
        var actionable = remaining
            .Where(x => ItemAxes.CanNudge(x, today))
            .Where(x => IsActionableNow(x, now))
            .ToList();

        if (actionable.Count <= 1)
        {
            return actionable;
        }

        // 已经开了头的优先：同样是"再回来做"，接着做比重新开始容易得多。
        // 这正是这个软件要对抗的东西——难的从来不是做，是开始。
        return actionable
            .OrderBy(x => x.Status == ItemStatus.InProgress || x.StartedAt is not null ? 0 : 1)
            .ToList();
    }

    /// <summary>
    /// 挑一件现在能被点名的事。挑不出来返回 null。
    /// </summary>
    /// <param name="remaining">
    /// 今天还没做完的事。**调用方负责筛 status**——这里不再筛一次，
    /// 是因为"跳过的不算、放弃的不算"这个口径属于完成率，不该在两处各写一遍。
    /// </param>
    /// <param name="now">现在。由调用方传入而不是内部取，为了可预测。</param>
    public static Item? Pick(IReadOnlyList<Item> remaining, DateTimeOffset now) =>
        Actionable(remaining, now).FirstOrDefault();

    /// <summary>
    /// 这一件现在能不能动手。
    ///
    /// 【三条规则为什么是这三条】
    /// 1. 已经开始过 → 能。不管它排的几点，人都摸过了。
    /// 2. 排了将来才开始 → 不能。这就是那条睡前任务栽的地方。
    /// 3. 全天事件 → 能。"生日""今天内交"这类没有钟点，压根不被第 2 条覆盖。
    /// </summary>
    private static bool IsActionableNow(Item item, DateTimeOffset now)
    {
        // 已经开了头，或者压根没排时间（纯待办）：随时能做。
        if (item.StartedAt is not null || item.StartAt is null || item.IsAllDay)
        {
            return true;
        }

        // 排了具体时间但还没到点（留出 GraceMinutes 的缓冲）：现在做不了。
        return item.StartAt.Value <= now.AddMinutes(GraceMinutes);
    }
}
