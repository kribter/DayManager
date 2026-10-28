namespace DayManager.Core.Intervention;

/// <summary>
/// 此刻前台是什么、这一段从几点开始。
///
/// 【为什么给的是 StartedAt 而不是"已经几分钟"】
/// 一个现成的分钟数在传出去的那一刻就开始过期了。调用方拿到它再去判断
/// "够不够 30 分钟"，中间差的可能是几十秒到几分钟。
/// 给起点、让读的人自己减，永远不会拿到一个已经过时的数字。
/// </summary>
public sealed record CurrentUsageSnapshot(
    string AppId,
    string AppName,
    DateTimeOffset StartedAt,
    string? Title)
{
    /// <summary>这一段连续用了多久（分钟）。未来的时间点算出 0，不会算出负数。</summary>
    public int MinutesSince(DateTimeOffset now)
    {
        var span = now - StartedAt;
        return span <= TimeSpan.Zero ? 0 : (int)span.TotalMinutes;
    }
}

/// <summary>
/// "现在在用什么"的来源。
///
/// 【为什么要抽一个接口，而不是让干预服务直接拿 WindowsUsageTracker】
/// 干预服务是共享代码，Android 上也要用；而采集器每个平台一份实现
/// （Windows 是 user32.dll，Android 是 UsageStatsManager）。
/// 如果干预服务直接依赖 Windows 那个类，Android 端连编译都过不去。
/// 接口里只有一个只读属性，实现起来几乎没有成本。
/// </summary>
public interface ICurrentUsageSource
{
    /// <summary>现在这一刻的应用。没人用（空闲、锁屏、刚启动）时为 null。</summary>
    CurrentUsageSnapshot? Current { get; }
}

/// <summary>用户在通知上点了什么。</summary>
public enum NoticeAction
{
    /// <summary>点了「知道了」。什么都不改，只是收起来。</summary>
    Dismissed = 0,

    /// <summary>点了「今天别再提了」。要记下来，今天不再提醒这个应用。</summary>
    Snooze = 1
}
