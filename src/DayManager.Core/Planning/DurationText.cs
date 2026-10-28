namespace DayManager.Core.Planning;

/// <summary>
/// 把"多少分钟"说成人话。
///
/// 【为什么要单独放一个类，而不是在每个 ViewModel 里各写一遍】
/// 估时要出现在三个地方：拆解页的每条任务、每天的小计、今日清单的一行。
/// 三处各写一遍的结果是格式不一致——一处显示"90 分钟"，另一处显示"1.5 小时"，
/// 而且以后想改成"1 小时半"要改三个地方。集中一处，格式永远一致。
///
/// 【放在 Core 而不是 App】
/// Core 是两端共用的层。将来手机端显示同样的估时，用的是同一个实现，
/// 不会出现"手机上显示 90 分钟、电脑上显示 1小时30分"。
/// </summary>
public static class DurationText
{
    /// <summary>
    /// 格式化一个时长。超过一小时会拆成"1 小时 30 分"，整小时省略分钟部分。
    ///
    /// 【为什么不直接用"90 分钟"】
    /// 人对"90 分钟"没有直觉多长，但对"1 小时半"有。
    /// 尤其是判断"今天排得会不会太满"时，小时是更自然的单位。
    /// </summary>
    public static string Format(int minutes)
    {
        if (minutes <= 0)
        {
            return string.Empty;
        }

        if (minutes < 60)
        {
            return $"{minutes} 分钟";
        }

        var hours = minutes / 60;
        var rest = minutes % 60;

        // 整小时就不要拖一个 "0 分" 在后面，念起来别扭。
        return rest == 0 ? $"{hours} 小时" : $"{hours} 小时 {rest} 分";
    }

    /// <summary>
    /// 带"约"字的版本，用在合计上——合计本来就是估时相加，不是精确值。
    /// </summary>
    public static string About(int minutes) =>
        minutes <= 0 ? "未估时" : "约 " + Format(minutes);
}
