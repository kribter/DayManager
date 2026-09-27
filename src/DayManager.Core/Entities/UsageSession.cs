using DayManager.Core.Enums;

namespace DayManager.Core.Entities;

/// <summary>
/// 设备使用会话。
///
/// 【为什么叫"会话"，而不是每秒 / 每分钟记一条】
/// 如果每秒记一条，一天就是 8 万多条，一年下来几千万条，
/// 手机端根本扛不住，而且这些信息毫无价值——没人关心你第 12345 秒在看什么。
/// 真正需要知道的只是"从几点到几点用了什么应用"。
/// 所以采集时做聚合：连续使用同一个应用（且窗口标题没变）就合并成一条记录，
/// 切走了就结束这条会话。数据量能降三个数量级。
/// </summary>
public class UsageSession : SyncEntityBase
{
    /// <summary>
    /// 数据来源平台。
    ///
    /// 【为什么两端能力不对等】
    /// Windows 可以拿到进程名 + 窗口标题（能知道你在 VSCode 里打开的是哪个项目）；
    /// Android 的 UsageStatsManager 只能拿到包名，窗口标题需要无障碍服务，
    /// 代价过大所以一期不做。同一个表用可空字段兼容这种不对等。
    /// </summary>
    public DevicePlatform Platform { get; set; }

    /// <summary>
    /// 应用标识：Windows 上是进程名，Android 上是包名。
    /// 程序内部判断身份用这个字段，不要用它做展示（对用户不可读）。
    /// </summary>
    public string AppId { get; set; } = string.Empty;

    /// <summary>对用户可读的应用名（例如 "Visual Studio Code"）。</summary>
    public string? AppName { get; set; }

    /// <summary>
    /// 窗口标题。Windows 端专有，Android 端为 null。
    /// 这是"采集到最细"的主要信息来源。
    /// </summary>
    public string? WindowTitle { get; set; }

    public DateTimeOffset StartAt { get; set; }

    public DateTimeOffset EndAt { get; set; }

    /// <summary>
    /// 持续时长（秒）。
    ///
    /// 【为什么明明可以用 EndAt - StartAt 算出来，还要单独存】
    /// 算出来的话每次统计都要做一次减法再求和，数据量大了很吃 CPU；
    /// 更关键的是，会话中间可能因为设备休眠产生"空洞"（看起来在用其实屏幕是关的），
    /// 单独存一个经过清洗的时长，比单纯的时间差更符合真实情况。
    /// </summary>
    public int DurationSeconds { get; set; }

    /// <summary>
    /// 是否属于娱乐消遣类应用。
    ///
    /// 【为什么冗余存这个布尔值，而不是查询时去对照名单】
    /// 因为名单是会变的——今天你把某个应用标为非娱乐，半年后再看历史统计，
    /// 如果当时没记录，那份历史数据就会被"追溯篡改"，导致历史完成率和趋势失真。
    /// 采集当时就固化判定结果，历史才具有可比性。
    /// </summary>
    public bool IsEntertainment { get; set; }
}
