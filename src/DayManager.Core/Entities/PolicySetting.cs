using DayManager.Core.Enums;

namespace DayManager.Core.Entities;

/// <summary>
/// 一个"会自己变"的参数。
///
/// 【为什么参数要单独存一张表，而不是写成常量】
/// 决策 D8 定了：这个软件里不存在"设一次就固定"的旋钮。
/// 每天可用分钟数、排程保守系数、干预阈值、AI 的语气……都要随用户变化动态调整。
/// 写成代码常量就意味着"改一次得重新编译"，那根本不叫动态调整。
///
/// 【为什么必须存数据库，不能只活在对话上下文里】
/// AI 每次被唤起都要带上当前策略。策略只放在会话里的话，
/// 上一次对话调过的参数在下一次对话里就消失了，
/// 表现为"它一会儿说每天 2 小时，一会儿说每天 40 分钟"，而且说不出为什么。
/// 存库之后每次调用读同一份，行为才一致。
///
/// 【为什么每一项都要记来源和理由】
/// 用户手动改过的项，AI 不得再自动覆盖（2.7）——没有 Source 就分不清是谁设的。
/// 而 Reason 是给"必须告知 + 可回滚"这条约束用的：
/// AI 每调一次参数都要说得出依据，理由存在这里，界面上才显示得出来。
/// </summary>
public class PolicySetting : SyncEntityBase
{
    /// <summary>
    /// 参数名，例如 "daily_minutes" / "conservative_factor" / "intervention_threshold_minutes"。
    /// 唯一，靠它取值。
    /// </summary>
    public string Key { get; set; } = string.Empty;

    /// <summary>
    /// 当前值（统一存字符串，用时再解析）。
    ///
    /// 【为什么不用强类型字段】
    /// 参数种类杂（整数、小数、时段、语气这种枚举式文本），
    /// 为每种开一个字段等于把表结构写死，加一个参数就要动迁移。
    /// 存字符串换来"加参数不用改表"，代价是读取时要解析——
    /// 而读取只会发生在少数几个地方，集中处理好过到处改表。
    /// </summary>
    public string Value { get; set; } = string.Empty;

    /// <summary>
    /// 冷启动的默认值。
    ///
    /// 【为什么要把默认值一直留着】
    /// 一是"恢复默认"需要它；二是校准要有基线——
    /// 调整幅度必须有上下限（2.7 第三条），上下限是相对默认值算的，
    /// 没有它，AI 一路调下去就不会知道已经偏离多远了。
    /// </summary>
    public string DefaultValue { get; set; } = string.Empty;

    /// <summary>这个值是谁定的：默认 / AI / 用户。</summary>
    public PolicySource Source { get; set; } = PolicySource.Default;

    /// <summary>
    /// 最近一次调整的依据，给人看的一句话。
    /// 例如："最近 7 天实际完成 60 分钟/天，低于设定的 120，下调以避免排不完"。
    /// </summary>
    public string? Reason { get; set; }

    /// <summary>
    /// AI 上次调整这个参数的时间。
    ///
    /// 【为什么需要它】
    /// 2.7 定了一次只调一个参数、7 天一轮。没有这个时间戳，
    /// AI 每次被唤起都可能再调一次同一个参数，7 天观察窗口形同虚设——
    /// 参数会天天变，用户察觉不到为什么，软件就变得不可捉摸。
    /// </summary>
    public DateTimeOffset? LastAiAdjustAt { get; set; }
}
