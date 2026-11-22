using DayManager.Core.Entities;
using DayManager.Core.Enums;

namespace DayManager.Core.Planning;

/// <summary>
/// 一条「只有截止日的事」相对今天落在哪一格。
///
/// 【11-21 事故后改版：不再推断，改读 Shape】
/// 旧版这里的判据是 StartAt 空 + DueAt 在未来，注释写着「不用新字段、不用迁移」。
/// 结果把 18 条按天摊出来的待办（同样 StartAt 空 + DueAt 在未来）全收进了常驻带，
/// 显示成「还有 N 天 · X月X日 截止」。措辞不准只是表象，
/// 真正的错是**形态从来没被存下来，只能靠时间字段的组合去猜**。
/// 现在第一句判据是 Shape == DeadlineOnly，那是创建时写死的声明。
///
/// 【三条完成语义仍然不是状态机】
/// 提前完成收起 / 到截止日当天转正经待办 / 过期滚欠账，
/// 依旧是 GetDayListAsync 的 due 分支在 DueAt 时间轴上自然滑动的结果：
///   DueAt 在未来   → 不满足 due 的「DueAt &lt; 这天结束」，不进今天清单
///   DueAt 落在今天 → 命中 due 分支，变成正经待办
///   DueAt 过去且没做 → 命中 due 分支，滚成逾期欠账
///   标了 Done      → due 排除、completedThatDay 接住，从所有天一起收起
/// Shape 只负责「是不是这一整段」，不负责「今天算不算」——后者归时间轴。
///
/// 【为什么是纯函数】
/// 参数全是值，不碰数据库也不碰时钟。冒烟能直接逐条断言边界，
/// 不用等真跑起界面才发现「差一天」。
/// </summary>
public static class ItemPresence
{
    /// <summary>按本地时间取这一天。与清单口径一致，不用 DayBoundary 的 5 点起点。</summary>
    public static DateOnly LocalDay(DateTimeOffset at) =>
        DateOnly.FromDateTime(at.ToLocalTime().DateTime);

    /// <summary>
    /// 这条此刻是不是「还悬着、没到截止日」—— 常驻可见但不算今天该做完。
    ///
    /// 【第一句判据必须是 Shape】
    /// 定日（DayTask）哪怕 DueAt 在很远的未来，也不是常驻项：
    /// 那是「安排在未来某天做」，不是「只有截止日」。这正是旧版猜错的地方。
    /// </summary>
    public static bool IsFloatingDeadline(
        ItemShape shape,
        VerdictMode mode,
        DateTimeOffset? dueAt,
        ItemStatus status,
        DateOnly today)
    {
        if (shape != ItemShape.DeadlineOnly)
        {
            return false;
        }

        // 守住类永不判成败，也不该挂在常驻带里被催。
        if (mode == VerdictMode.Never)
        {
            return false;
        }

        if (dueAt is null)
        {
            return false;
        }

        // 做完和放弃都已经了结，不再悬着。
        // 「进行中」「今天跳过」仍算悬着：动过手、推过一次，都还没到终点。
        if (status is ItemStatus.Done or ItemStatus.Abandoned)
        {
            return false;
        }

        // 今天到期 = 正经待办；过去 = 逾期欠账。两者都归「今天该做完」那一带。
        return LocalDay(dueAt.Value) > today;
    }

    /// <summary>便捷重载：直接吃实体，读它的 Shape / VerdictMode。仓储和界面走这个。</summary>
    public static bool IsFloatingDeadline(Item item, DateOnly today) =>
        IsFloatingDeadline(item.Shape, item.VerdictMode, item.DueAt, item.Status, today);

    /// <summary>离截止日还剩几天。只在常驻态调用，恒 >= 1。</summary>
    public static int DaysLeft(DateTimeOffset dueAt, DateOnly today) =>
        LocalDay(dueAt).DayNumber - today.DayNumber;

    /// <summary>
    /// 常驻带副标题。「还有 6 天 · 12月15日 截止」。
    ///
    /// 【为什么天数要代码算】
    /// 同「时间差交给代码」那条硬规矩：不让它（也不让模型）自己推。
    /// 跨年补上年份，免得 1 月看到「12月15日」以为早过了。
    /// </summary>
    public static string DeadlineLine(DateTimeOffset dueAt, DateOnly today)
    {
        var local = dueAt.ToLocalTime();
        var days = DaysLeft(dueAt, today);
        var date = local.Year != today.Year
            ? $"{local.Year}年{local.Month}月{local.Day}日"
            : $"{local.Month}月{local.Day}日";

        return $"还有 {days} 天 · {date} 截止";
    }
}
