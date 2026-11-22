using DayManager.Core.Enums;

namespace DayManager.Core.Entities;

/// <summary>
/// 统一记录：日程和待办合并在同一张表里。
///
/// 【为什么不分成 Event 和 Todo 两张表】
/// 这是你的选择，也是我认为对的做法。分开两张表意味着：
/// 两套增删改查代码、两套界面、两套统计逻辑，还得处理"一个事项既是日程又有待办属性"的尴尬。
/// 合并的代价是字段会有空值，收益是代码量减半、逻辑统一。
///
/// 【旧写法已作废：靠可空的时间字段区分形态】
/// 这里原先写的是「有 start_at → 日程，只有 due_at → 待办」。11-21 出了事故：
/// 「15 号那天做」和「15 号是最后期限」在库里长得一模一样，
/// 常驻带靠推断把 18 条按天摊的待办全收了进去。
/// 根不是判断写得不够细，是**形态从来没被当成数据存过**。
/// 现在形态由 Shape 显式声明，StartAt / DueAt 只负责「什么时刻」，不负责「这是什么」。
/// 见 Enums.cs 的 ItemShape 说明。
/// </summary>
public class Item : SyncEntityBase
{
    public string Title { get; set; } = string.Empty;

    public string? Notes { get; set; }

    /// <summary>
    /// 这件事是「该做的」还是「想做的」。
    ///
    /// 【默认值为什么是 MustDo】
    /// 大多数记录属于这一类，而且默认值必须是一个"说错了也无妨"的方向：
    /// 把想做的事误标成该做的，后果只是它暂时没被当成奖励推给你；
    /// 反过来把该做的误标成想做的，一件有截止日期的正事会变成"奖励"，
    /// 出现在你刚做完另一件事的时候——那才是真的乱套。
    /// </summary>
    public ItemKind Kind { get; set; } = ItemKind.MustDo;


    // ---------- 三根轴：形态 / 完成方式 / 判成败 ----------
    //
    // 这三根彼此独立，一条记录在每根上各有一个答案，可以任意组合。
    // 详见 docs/项目全景.md 与 Enums.cs 里每个枚举的说明。

    /// <summary>
    /// ① 落哪天（形态）。默认定日，是历史数据回填时最保守的一档。
    /// 创建入口必须显式赋值，别让调用方靠默认猜。
    /// </summary>
    public ItemShape Shape { get; set; } = ItemShape.DayTask;

    /// <summary>
    /// ③ 怎么算完成。默认做一次；容器写 ByChildren，重复事项写 PerOccurrence。
    /// </summary>
    public CompletionMode CompletionMode { get; set; } = CompletionMode.Once;

    /// <summary>
    /// 判不判成败。默认判；只有「守住」类约束写 Never。
    /// 「暂时不判」不存，由 Shape=DeadlineOnly + 截止日时间轴算出来。
    /// </summary>
    public VerdictMode VerdictMode { get; set; } = VerdictMode.Judged;


    // ---------- 时间：只负责「什么时刻」，不负责「这是什么」 ----------

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

    /// <summary>
    /// 跳过发生在什么时候。
    ///
    /// 【为什么必须记这个时刻，光有 Skipped 状态不够】
    /// "跳过"只对那一天有效：今天跳过，明天它应该照常回来。
    /// 但数据库里状态就只有一个值，第二天打开软件时无从判断
    /// 这条是"被跳过了"还是"该重新出现了"。
    /// 记下时刻之后，判定规则就成立了（见 ItemRepository.GetDayListAsync）：
    ///   跳过时刻 ≥ 这一天的零点  → 这一天它被跳过，不显示
    ///   跳过时刻 < 这一天的零点  → 那是以前的事了，今天照常显示
    /// 这样"明天自动回来"是查出来的，不是靠每天跑一个任务把状态改回来——
    /// 少一个后台任务，就少一类"它没跑起来"的故障。
    /// </summary>
    public DateTimeOffset? SkippedAt { get; set; }

    /// <summary>
    /// 跳过的原因（一句话，可空）：生病 / 有事 / 被别的事占了。
    ///
    /// 【为什么要让用户能写一句】
    /// 同样是"没做"，"今天被临时会议占了"和"就是不想动"是完全不同的信号。
    /// 前者是外部环境，后者才是需要 AI 出面谈的。
    /// 没有这句话，AI 只能看到冷冰冰的"跳过 3 次"，无从区分该安慰还是该追问。
    /// 也给用户自己留个台阶——写下来的那一刻，就不是"我又没做"，而是"我今天有理由"。
    /// </summary>
    public string? SkipReason { get; set; }

    /// <summary>
    /// 累计被跳过了几次。
    ///
    /// 【为什么要单独计数，而不是去数 SkippedAt】
    /// SkippedAt 只保留最近一次的时刻（它要用来判断"现在是不是跳过态"），
    /// 会被下一次跳过覆盖。而"这条被跳过太多次"是一个重要的信号：
    /// 它通常不是拖延，是**这件事本身就不该做**——
    /// 每次看到它都决定跳过，却每天把它搬回来，那是软件在帮忙自欺。
    /// AI 要靠这个数字主动提出来谈一次，而不是默默让它一直滚回来。
    /// </summary>
    public int SkipCount { get; set; }


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


    // ---------- ② 是不是容器：自引用层级 ----------
    //
    // ② 不新增枚举，因为「是不是容器」是**关系**不是取值：
    // 有子项的就是容器，没子项的就是叶子。一个节点既可以是别人的子项、
    // 又是别人的容器，所以这根轴天生可递归（数学竞赛 → 第一章 → 做题）。
    //
    // 【为什么当初留了字段却没启用】
    // ProjectRepository.cs 开头记着：父子都落在 items 一张表时，
    // 今日清单会把父子两条都查出来，同一件事在界面上出现两次。
    // 所以早期改用「项目独立一张表」绕开。现在启用递归，必须同时做到：
    //   今日清单只出叶子（GetDayListAsync 过滤掉有子项的记录）
    //   进度 / 完成率分母只数叶子（容器不重复计数）
    // 这两条是本轮一起落的，别只启用字段不过滤。

    /// <summary>所属项目。可空，允许存在不挂在任何项目下的零散任务。</summary>
    public Guid? ProjectId { get; set; }

    /// <summary>
    /// 父任务（同一张 items 表内的自引用）。非空表示我是某个容器/阶段性目标的下级。
    /// 阶段性目标本身也是 Item，可以再挂更下一级，深度不固定。
    ///
    /// 【注意别和 RecurrenceParentId 混】
    /// 那个是「重复日程的单次覆盖」，这个是「任务层级」，两个语义完全不同的父子关系。
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
