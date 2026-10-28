namespace DayManager.Data;

/// <summary>
/// 把"某一天"换算成确切时刻的工具。
///
/// 【为什么要单独抽出来】
/// 这类换算散落在各个仓储里早晚出错：一处按零点算、一处按八点算，
/// 表现出来就是"同一条任务在手机上属于今天、在电脑上属于明天"。
/// 抽成一处之后，所有"这一天从什么时候开始、到什么时候结束"的判断都只有一个答案。
///
/// 【为什么用本地时区而不是 UTC】
/// 这里算的是"用户所在地区的这一天"，必须用当地时区才有意义——
/// 对你来说"今天"是从北京时间零点开始的，不是从 UTC 零点开始的。
/// 至于跨时区比较会不会出错，那件事在 AppDbContext 里已经解决了：
/// 所有时间入库前都会转成 UTC 毫秒，数据库里存的是绝对时刻。
/// 这里只负责把"本地的一天"翻译成一个正确的瞬间。
/// </summary>
internal static class DateHelper
{
    /// <summary>这一天的零点（带本地时区偏移）。</summary>
    public static DateTimeOffset StartOfDay(DateOnly day)
    {
        var local = day.ToDateTime(TimeOnly.MinValue);
        var offset = TimeZoneInfo.Local.GetUtcOffset(local);
        return new DateTimeOffset(local, offset);
    }

    /// <summary>
    /// 这一天的最后一刻（23:59:59.999）。
    ///
    /// 【为什么要取"最后一刻"而不是零点】
    /// 用作任务的截止时间时，取零点的话这条任务在当天刚开始的那一瞬间就算逾期了，
    /// 界面上会显示成"已过期"，而实际上它有一整天的时间去做。
    /// 取当天最后一刻，它才会老老实实地待在当天清单里直到这天结束。
    /// </summary>
    public static DateTimeOffset EndOfDay(DateOnly day)
    {
        return StartOfDay(day).AddDays(1).AddMilliseconds(-1);
    }
}
