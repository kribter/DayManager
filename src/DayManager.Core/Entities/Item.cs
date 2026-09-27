using DayManager.Core.Enums;

namespace DayManager.Core.Entities;

/// <summary>
/// 统一记录：日程和待办合并在同一张表里。
///
/// 【为什么不分成 Event 和 Todo 两张表】
/// 这是你的选择，也是我认为对的做法。分开两张表意味着：
/// 两套增删改查代码、两套界面、两套统计逻辑，还得处理"一个事项既是日程又有待办属性"的尴尬。
/// 合并之后靠"可空的时间字段"来区分形态：
///   有 start_at/end_at        → 它是个有具体时间的日程（比如"9 点开会"）
///   只有 due_at 或什么都没有  → 它是个待办（比如"交报告"）
/// 代价是字段会有空值，收益是代码量减半、逻辑统一。
/// </summary>
public class Item : SyncEntityBase
{
    public string Title { get; set; } = string.Empty;

    public string? Notes { get; set; }

    // ---------- 时间：三个字段表达所有形态 ----------

    /// <summary>
    /// 开始时间，可空。
    ///
    /// 【为什么可空】
    /// 待办事项可能压根没有具体时间，只有"今天搞定"这种模糊意图。
    /// 强行要求填时间会导致你懒得记录，反而破坏数据完整性。
    /// </summary>
    public DateTimeOffset? StartAt { get; set; }

    /// <summary>
    /// 结束时间，可空。
    ///
    /// 【注意与 StartAt 的搭配】
    /// 只有在 StartAt 有值的情况下 EndAt 才有意义。
    /// 有 StartAt 但没有 EndAt 时，表示"有个开始时间但没定多久"，
    /// 这时 UI 上应该用 DurationMinutes 或默认时长来兜底。
    /// </summary>
    public DateTimeOffset? EndAt { get; set; }

    /// <summary>
    /// 截止时间，可空。面向"最晚什么时候必须完成"，和"什么时候开始做"是两回事。
    /// </summary>
    public DateTimeOffset? DueAt { get; set; }

    /// <summary>
    /// 是否为全天事件（比如"生日""放假"这种不需要精确到钟点的）。
    /// </summary>
    public bool IsAllDay { get; set; }

    /// <summary>
    /// 这条记录创建时所属的时区（IANA 格式，例如 "Asia/Shanghai"）。
    ///
    /// 【为什么存了 DateTimeOffset 还要再存一个时区】
    /// DateTimeOffset 只能记录"这一个瞬间相对于 UTC 的偏移"，记录的却是具体地点。
    /// 举个实际会踩的坑：你定了"每天早上 9 点跑步"，出差到了另一个时区，
    /// 正确的行为是当地时间的 9 点响铃，而不是北京时间 9 点对应的那个瞬间。
    /// 只有把创建时的时区一起存下来，重复日程才能在任意时区里算对下一次触发时间。
    /// </summary>
    public string TimeZoneId { get; set; } = "Asia/Shanghai";

    /// <summary>
    /// 计划时长（分钟）。有时人们会说"这事大概要两小时"，但不指定几点开始。
    /// </summary>
    public int? DurationMinutes { get; set; }

    // ---------- 状态：抗拖延的核心数据 ----------

    public ItemStatus Status { get; set; } = ItemStatus.Pending;

    /// <summary>
    /// 首次真正开始的时间。
    ///
    /// 【这是整个项目最关键的字段之一】
    /// 启动延迟 = StartedAt - 创建时间。
    /// 你说自己的问题是"知道该做但启动不了"，那衡量有没有改善的唯一办法，
    /// 就是把"从想到到开始"这段间隔量化记录下来。
    /// 没有这个字段，我们只能看到"完成率"，而完成率是结果指标，
    /// 启动延迟才是你这个具体问题对应的过程指标。
    /// </summary>
    public DateTimeOffset? StartedAt { get; set; }

    /// <summary>完成时间。统计完成分布、生成每日总结时要用。</summary>
    public DateTimeOffset? CompletedAt { get; set; }

    // ---------- 估算与实际：AI 排程的校准依据 ----------

    /// <summary>你预估要花多久。</summary>
    public int? EstimateMinutes { get; set; }

    /// <summary>
    /// 实际花了多久。
    ///
    /// 【这个字段是 AI 不忽悠人的基础】
    /// 你说自己常常高估能力，给自己排太多任务。
    /// 如果 AI 排程时采用你的主观估计，它只会把你的高估原样放大。
    /// 正确做法是：AI 拿历史上同类任务的实际耗时作为依据。
    /// 所以每条做完的事都必须回填这里，数据攒起来之后 AI 才会越来越准。
    /// </summary>
    public int? ActualMinutes { get; set; }

    /// <summary>
    /// 最小启动步骤：两分钟内能做完的第一步（例如"打开文档写个标题"）。
    ///
    /// 【为什么需要它】
    /// 启动困难的实质是"开始"这个动作的心理成本太高。
    /// 把大任务显式拆出一个极小的第一步，是在降低启动门槛，
    /// 而不是像普通提醒那样只在旁边喊你去做。
    /// </summary>
    public string? MinimalStep { get; set; }

    // ---------- 重复日程 ----------

    /// <summary>
    /// 重复规则，使用 RFC 5545 的 RRULE 格式（例如 "FREQ=WEEKLY;BYDAY=MO,WE"）。
    ///
    /// 【为什么不自己定义格式】
    /// RRULE 是日历领域的通用标准，成熟的第三方库（我们用 Ical.Net）可以直接解析展开。
    /// 自己造规则不仅要写解析器，还要处理"每月最后一个工作日"这种变态用例，必定踩坑。
    /// </summary>
    public string? RRule { get; set; }

    /// <summary>
    /// 排除的日期（JSON 数组）。比如重复日程里某一次被单独删掉了。
    ///
    /// 【为什么单独存，而不是删掉那一条】
    /// 重复日程在数据库里只有一条母记录，单次 occurrences 是算出来的。
    /// 要跳过某一次，必须有个地方记录"这个日期不算"，否则下次展开时它又会冒出来。
    /// </summary>
    public string? ExDates { get; set; }

    /// <summary>
    /// 指向重复日程的母记录。非空时表示"我是某一单次实例的覆盖数据"。
    ///
    /// 【为什么这个字段要叫 RecurrenceParentId 而不是 ParentId】
    /// 因为项目层级里也有父子关系（ParentTaskId）。
    /// 两个语义完全不同的父子关系如果都叫 parent，
    /// 写代码时极易混淆，一旦搞错会产生非常难查的数据错误。名字长一点，但绝对值得。
    ///
    /// 【这个设计解决什么问题】
    /// 改重复日程时有三种语义："只改这一次"、"改这次及以后"、"改全部"。
    /// "只改这一次"的实现方式就是新建一条覆盖记录，挂到母记录上，
    /// 母记录本身保持不变，这样未来的实例不受影响。
    /// </summary>
    public Guid? RecurrenceParentId { get; set; }

    /// <summary>
    /// 这条覆盖记录对应的是原重复序列中的哪一次（原始计划开始时间）。
    /// 靠它才能在展开时把覆盖数据替换到正确的那一次上。
    /// </summary>
    public DateTimeOffset? OriginalStart { get; set; }

    // ---------- 两级结构 ----------

    /// <summary>所属项目。可空，允许存在不挂在任何项目下的零散任务。</summary>
    public Guid? ProjectId { get; set; }

    /// <summary>
    /// 父任务。用于在任务下面再挂一层子任务（做大目标分解时用，一期界面可以先不做）。
    /// </summary>
    public Guid? ParentTaskId { get; set; }

    /// <summary>所属项目（导航属性，便于查询时一次性带出项目信息）。</summary>
    public Project? Project { get; set; }

    // ---------- 组织方式 ----------

    /// <summary>
    /// 标签，JSON 数组字符串，例如 ["深度工作","外出"]。
    ///
    /// 【为什么用 JSON 字符串而不是建标签表】
    /// 建独立的标签表 + 关联表才符合数据库范式，查询也更快；
    /// 但代价是多两张表和一堆联表查询。你只有自己一个人用，
    /// 数据量极小，用 JSON 换来的是"改标签结构不用改表"，前期性价比高得多。
    /// 如果以后真需要"按标签筛选"的复杂查询，再拆表也不迟。
    /// </summary>
    public string? Tags { get; set; }

    public Priority Priority { get; set; } = Priority.Normal;

    /// <summary>
    /// 提醒配置，JSON 数组，例如 [{"offsetMinutes":15,"channel":"notification"}]。
    /// 同样是为了避免为了"提前多久提醒"再加一张表。
    /// </summary>
    public string? Reminders { get; set; }

    /// <summary>
    /// 排序序号。同一天多项日程时的展示顺序，
    /// 让你可以手动把最重要的一条摆在最上面。
    /// </summary>
    public int SortOrder { get; set; }
}
