namespace DayManager.Core.Enums;

/// <summary>
/// 一条记录（日程或待办）的生命周期状态。
/// </summary>
public enum ItemStatus
{
    /// <summary>还没开始</summary>
    Pending = 0,

    /// <summary>
    /// 进行中：已经开工，但还没做完。
    ///
    /// 【为什么不能只有 pending 和 done 两种状态】
    /// 这个状态是为你的核心痛点设计的。你说过瓶颈是"知道该做但启动不了"，
    /// 如果只有"未开始/已完成"，那么做到一半的事情在数据上和完全没碰过是一样的，
    /// 既没法告诉你"你已经迈出第一步了"，也没法统计启动到底花了多久。
    /// 有了这个状态，"开始"本身就成为一个值得记录的进步。
    /// </summary>
    InProgress = 1,

    /// <summary>已完成</summary>
    Done = 2,

    /// <summary>
    /// 放弃 / 取消。
    ///
    /// 【为什么要有"取消"而不是让用户直接删除】
    /// 放弃和删除在数据意义上是完全不同的：删除是"这条记录是误建的"，
    /// 取消是"这件事确实存在过，但我决定不做了"。
    /// 保留取消状态，AI 才能从历史里发现"你常常放弃这一类任务"这类规律。
    /// </summary>
    Cancelled = 3
}

/// <summary>
/// 优先级。刻意只做三档。
///
/// 【为什么不做五档或者"重要紧急四象限"】
/// 选项越多，每次填写的决策成本越高，最后要么全部标成最高优先级失去意义，
/// 要么干脆懒得填。三档是"够用"和"不烦"之间的平衡点。
/// </summary>
public enum Priority
{
    Low = 0,
    Normal = 1,
    High = 2
}

/// <summary>
/// 项目状态。
/// </summary>
public enum ProjectStatus
{
    /// <summary>进行中的项目</summary>
    Active = 0,

    /// <summary>
    /// 已归档。
    ///
    /// 【为什么归档而不是删除】
    /// 项目完成后删除会把里面的任务一并抹掉，历史完成率数据就断了。
    /// 归档保留全部数据，但不再出现在日常界面里。
    /// </summary>
    Archived = 1
}

/// <summary>
/// 记录这条数据来自哪类设备。
/// </summary>
public enum DevicePlatform
{
    Windows = 0,
    Android = 1
}

/// <summary>
/// 某一天的性质。干预引擎要根据它调整松紧。
/// </summary>
public enum DayType
{
    /// <summary>普通工作日</summary>
    Workday = 0,

    /// <summary>周末</summary>
    Weekend = 1,

    /// <summary>
    /// 法定节假日。
    ///
    /// 【为什么把节假日和周末分开】
    /// 你的干预规则里，"节假日可以彻底放松"和"周末可以松一点但还是要动一动"
    /// 是两种不同的松紧程度，合并成一个就没法区分了。
    /// </summary>
    Holiday = 2,

    /// <summary>
    /// 调休上班日。
    ///
    /// 【为什么要专门定义这一个】
    /// 这是中国特色：为了凑长假，某些周日要正常上班，某些周五反而放假。
    /// 如果只按"周六周日就是周末"来判断，调休那天的干预力度会错得离谱——
    /// 明明要上班，软件却用周末的宽松标准对待你。
    /// </summary>
    AdjustedWorkday = 3
}
