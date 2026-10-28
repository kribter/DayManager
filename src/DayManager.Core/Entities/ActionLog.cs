using DayManager.Core.Enums;

namespace DayManager.Core.Entities;

/// <summary>
/// 一次写操作的完整记录。撤销就是靠它。
///
/// 【为什么必须有这张表】
/// AI 会犯错，而且它的错是**批量**的——一句话可能改十条记录。
/// 决策 D4 定了"永久可撤，且撤销入口在界面上、不经过 AI"，
/// 这张表就是那句话能兑现的唯一前提：没有改之前的样子，就无从撤回去。
///
/// 【为什么是"快照"而不是"反向操作"】
/// 另一种做法是记录反向操作（把日期减一天、把状态改回去），
/// 但它有个致命问题：撤销时必须假设"从记下到现在，中间没别的变化"。
/// 而中间可能已经变了（你自己改过、AI 又改过），反向操作就会算出错误的结果。
/// 存快照则永远是"回到那个时刻的样子"，语义明确，还能顺手做冲突检测。
///
/// 【为什么按批次】
/// AI 的一次动作会碰多条记录，撤销也必须以"这一次动作"为单位——
/// 否则撤一条留九条，剩下的九条是半套计划，比全部保留更乱。
/// 所以同一批操作共享一个 BatchId。
/// </summary>
public class ActionLog : SyncEntityBase
{
    /// <summary>
    /// 批次号。同一次 AI 动作（或同一次批量操作）产生的所有日志共享一个值。
    ///
    /// 【为什么不用"时间相近"来归批】
    /// 靠时间聚类判断"这几条是不是一批"看起来省事，
    /// 但两件事在同一秒内发生时就会张冠李戴，而且这种错误只在特定时机出现，极难复现。
    /// 一个显式分配的 Guid 没有任何歧义。
    /// </summary>
    public Guid BatchId { get; set; }

    /// <summary>谁做的。撤销只覆盖 AI 的操作（D4）。</summary>
    public ActionActor Actor { get; set; }

    /// <summary>做了什么类型的操作。决定撤销时走哪个反向动作。</summary>
    public ActionType Action { get; set; }

    /// <summary>
    /// 操作对象的类型（"items" / "projects" / "policy"）。
    ///
    /// 【为什么用字符串而不是枚举】
    /// 这张表将来还要记录别的对象，加一个枚举值就要动枚举、动迁移；
    /// 而撤销代码本来就要按类型分派，用字符串在这里反而不容易写错。
    /// 一期只有 items 真正被撤销，其余类型只记录、不提供撤销。
    /// </summary>
    public string TargetType { get; set; } = "items";

    /// <summary>被操作的记录 Id。</summary>
    public Guid TargetId { get; set; }

    /// <summary>
    /// 修改**之前**的完整快照（JSON）。撤销就是把它写回去。
    ///
    /// Create 操作时它为 null —— 那时"之前"是不存在，撤销的含义变成软删除。
    /// </summary>
    public string? BeforeJson { get; set; }

    /// <summary>
    /// 修改之后的快照（JSON）。
    ///
    /// 【为什么要存"之后"，光有"之前"不够吗】
    /// 撤销前要做冲突检测：拿"之后"和"现在的样子"比较，
    /// 如果不一样，说明这中间有别的人动过它，这时不能闷头覆盖，得先告诉用户。
    /// </summary>
    public string? AfterJson { get; set; }

    /// <summary>
    /// 给人看的一句话，例如"把 3 条任务改到下周"。
    ///
    /// 【为什么必须有一句人话】
    /// 撤销列表是给人看的。如果只显示"更新了 items 3 条"，
    /// 几天之后用户根本想不起来那是什么，撤销就成了盲选。
    /// 这句话由执行操作的一方（AI 或界面）负责写得具体。
    /// </summary>
    public string Summary { get; set; } = string.Empty;

    /// <summary>
    /// 撤销时间。非空表示这条已经被撤回了。
    ///
    /// 【为什么是打标记而不是删掉日志】
    /// 删掉的话，同一个批次会被撤两次（第二次找不到记录，静默什么都不做，
    /// 用户以为没撤成功于是反复点）。留着标记，第二次撤销时明确回答"已撤过了"。
    /// 而且保留撤销历史本身有价值：能看出 AI 的错误率到底有多高。
    /// </summary>
    public DateTimeOffset? UndoneAt { get; set; }
}
