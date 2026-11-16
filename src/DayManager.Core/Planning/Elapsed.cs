namespace DayManager.Core.Planning;

/// <summary>
/// 一件事"到目前为止花了多久"的算法。
///
/// 【为什么两个变量的加减法要单独抽出来】
/// 这同一个数字会算在三处：
///   列表里那句"已进行 25 分钟"、
///   点暂停时把这一段结清的时间、
///   勾完成时回填的 ActualMinutes。
/// 三处各写一遍的话，迟早出现"界面显示 25 分钟、库里记了 24 分钟"——
/// 谁都不报错，但数据自相矛盾，而且这种问题只能靠偶然发现。
/// 算法只有一份，三处必然一致。
///
/// 【为什么在这里必须是纯函数】
/// 纯函数才能被离线冒烟逐条断言。这一段算法承担了"你做事要花多久"这个画像数据，
/// 它错了 AI 会一路错下去——属于最该被测住的那类东西。
///
/// 【为什么颗粒度是分钟】
/// ActualMinutes 字段的单位就是分钟，AI 排程也只在这个粒度上讨论时间。
/// 记秒在这里没有用途：太精确的数字反而会让人去计较"这也算工作？"。
/// </summary>
public static class Elapsed
{
    /// <summary>
    /// 到 <paramref name="until"/> 为止，累计花了多少分钟。
    /// </summary>
    /// <param name="accumulated">
    /// 已经结清的各段时间之和（每暂停一次结一次）。可空，空算 0。
    /// </param>
    /// <param name="startedAt">
    /// 当前这一段从什么时候开始走。空表示这会儿没在计时（没开始，或暂停中）。
    /// </param>
    /// <param name="until">
    /// 算到哪一刻：正在进行的传"现在"，已经做完的传 CompletedAt。
    /// </param>
    public static int TotalMinutes(
        int? accumulated,
        DateTimeOffset? startedAt,
        DateTimeOffset until)
    {
        var banked = Math.Max(0, accumulated ?? 0);

        // 暂停态：startedAt 是空的，那段已经结进 accumulated 了。
        // 这时候不能再加——否则每算一次就凭空多一段。
        if (startedAt is null)
        {
            return banked;
        }

        var running = until - startedAt.Value;

        // 把系统时钟往回调过：间隔会是负数。
        // 负数不是"用了负的时间"，而是说明这段数据没法用，按 0 处理。
        if (running.TotalMinutes <= 0)
        {
            return banked;
        }

        return banked + (int)running.TotalMinutes;
    }

    /// <summary>
    /// 这个值该不该写进 ActualMinutes。
    ///
    /// 【为什么要有"取舍"这一步】
    /// 直接勾完成的情况根本没走过计时（间隔 0），
    /// 写个 0 进去等于替用户编了一个答案：
    /// AI 看到 ActualMinutes=0 会读成"这件事不用时间"，比留空更糟。
    /// 没数据就承认没数据。
    /// </summary>
    public static int? OrNullIfZero(int minutes) => minutes > 0 ? minutes : null;
}
