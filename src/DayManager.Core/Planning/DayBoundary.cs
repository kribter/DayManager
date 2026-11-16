namespace DayManager.Core.Planning;

/// <summary>
/// 「一天」从几点算起。
///
/// 【为什么需要它：按午夜切跟实际作息对不上】
/// 实测五天里有三天是跨午夜连续用的（到 00:51 / 01:05 / 00:45）。
/// 按日历日切的话，凌晨 1 点在程序眼里已经是"第二天"：
/// 开场白会在人还没睡的时候冒出来，而当天的日结又会把还没过完的一天结算掉。
/// 把起点挪到凌晨 5 点，凌晨那几个小时就仍归前一天——"昨晚还没收尾"，
/// 而不是"新的一天开始了"。
///
/// 【为什么默认 5 点】
/// 取一个"人基本都睡了"的时刻。太早（0 点）没意义，太晚（8 点）会把早起的人
/// 算成前一天。5 点是个不需要用户思考就能接受的默认值。
///
/// 【为什么不做全局替换——这是一次有意的取舍】
/// 代码里有二十多处算"今天"的地方（日程页、计划页、干预判定、提醒），
/// 它们现在工作正常，全改一遍的风险远大于收益。
/// 所以这个类**只在画像和日结这条新链路上用**，其余地方仍是日历日。
/// 代价是两套口径并存；收益是验证期不动正在跑的东西。
/// 等哪天要统一时，改的是这一个类，不用去二十多处找。
///
/// 【为什么必须是纯函数】
/// 跨午夜是它唯一存在的理由，而这种边界最容易写错又最难靠用发现。
/// 纯函数才能被离线冒烟逐条断言（跨天、起点前后一分钟、起点整点）。
/// </summary>
public static class DayBoundary
{
    /// <summary>默认的起点小时：凌晨 5 点之前的几个小时仍算前一天。</summary>
    public const int DefaultStartHour = 5;

    /// <summary>
    /// 某个时刻属于哪一天。
    /// </summary>
    /// <param name="moment">要判定的时刻。</param>
    /// <param name="startHour">一天从几点算起。传了非法值就退回默认。</param>
    public static DateOnly Resolve(DateTimeOffset moment, int startHour = DefaultStartHour)
    {
        var hour = Normalize(startHour);
        var local = moment.LocalDateTime;

        // 还没到起点 → 这一刻仍归昨天。
        // 凌晨 2 点 → 前一天；早上 6 点 → 当天。
        return local.Hour < hour
            ? DateOnly.FromDateTime(local).AddDays(-1)
            : DateOnly.FromDateTime(local);
    }

    /// <summary>
    /// 某一天覆盖的时间区间（左闭右开）：起点那刻 → 次日起点那刻。
    ///
    /// 【为什么要这个方法】
    /// 日结要按"这一天"去库里捞明细。有了边界之后，
    /// 一天的区间不再是 00:00–24:00，而是 05:00–次日 05:00。
    /// 这个换算写一次，日结和画像趋势共用，免得两处各算一遍。
    /// </summary>
    public static (DateTimeOffset Start, DateTimeOffset End) RangeOf(
        DateOnly day,
        int startHour = DefaultStartHour)
    {
        var hour = Normalize(startHour);

        var start = ToLocal(day.ToDateTime(new TimeOnly(hour, 0)));
        var end = ToLocal(day.AddDays(1).ToDateTime(new TimeOnly(hour, 0)));

        return (start, end);
    }

    /// <summary>
    /// 把本地时间转成带正确偏移的时刻。
    ///
    /// 【为什么不直接 new DateTimeOffset(local)】
    /// 那个构造会在偏移不合法时抛异常，而这里只是想表达"本地墙上时间"。
    /// 走 TimeZoneInfo 拿当前偏移，中国没有夏令时，够用。
    /// </summary>
    private static DateTimeOffset ToLocal(DateTime local)
    {
        var offset = TimeZoneInfo.Local.GetUtcOffset(local);
        return new DateTimeOffset(local, offset);
    }

    /// <summary>小时越界就退回默认值，不让调用方的一步失误把日期算飞。</summary>
    private static int Normalize(int startHour) =>
        startHour is >= 0 and <= 23 ? startHour : DefaultStartHour;
}
