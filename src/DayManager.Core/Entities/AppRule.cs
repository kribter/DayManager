using DayManager.Core.Enums;

namespace DayManager.Core.Entities;

/// <summary>
/// 一个应用归到哪一类：娱乐、工作，还是没标记。
///
/// 【为什么这件事必须落到库里，而不是写死在代码里】
/// "什么算娱乐"没有客观答案。同一个 B 站，看网课和看鬼畜是两件完全不同的事；
/// 同一个 Steam，装的是游戏还是建模工具，只有你知道。
/// 任何一份"标准娱乐名单"都只是近似，所以名单只能当默认值，最终必须由用户改。
///
/// 【为什么按进程名（AppId）而不是窗口标题】
/// 标题是内容，一直在变（"【某某】第 3 讲 - 哔哩哔哩"）。按标题判定等于每次都要重新认一遍，
/// 而且会把"用 B 站看课程"和"用 B 站刷视频"判成同一个东西——
/// 那正好是这份表要区分的两种情形。进程名在一段时间内是稳定的，适合当键。
///
/// 【为什么不和 UsageSession 合并成一张表】
/// 使用记录是"某天用过什么"，一小时一条、一年几十万条；
/// 这条规则是"这个应用算什么"，一个应用一辈子一条。
/// 混在一起会让每次统计都要跳过重复的规则行，而且改一次规则要动历史数据。
/// </summary>
public class AppRule : SyncEntityBase
{
    /// <summary>
    /// 进程名（小写、带 .exe），例如 "bilibili.exe" / "steam.exe"。
    /// 这是判定的键，唯一。
    /// </summary>
    public string AppId { get; set; } = string.Empty;

    /// <summary>
    /// 显示用的名字，例如 "哔哩哔哩"。进程名给用户看太生硬（"chrome.exe" 看不出是哪个）。
    /// 存一份是为了设置页不必再去查采集记录。
    /// </summary>
    public string AppName { get; set; } = string.Empty;

    /// <summary>
    /// 归类。
    ///
    /// 【为什么默认是 Neutral 而不是 Work】
    /// 默认成工作意味着"什么都没标记时，所有应用都不算娱乐"，
    /// 那干预永远不会触发；反过来默认娱乐则是把浏览器、编辑器全算成娱乐。
    /// Neutral 的语义是"还没定"：交给内置名单猜一次，猜不出来就不打扰。
    /// </summary>
    public AppKind Kind { get; set; } = AppKind.Neutral;
}
