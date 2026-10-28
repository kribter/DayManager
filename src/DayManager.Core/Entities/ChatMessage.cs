namespace DayManager.Core.Entities;

/// <summary>
/// 一条对话记录。
///
/// 【为什么要存下来】
/// 对话是这个软件的主界面，但它原先只活在内存里：关掉程序，
/// 昨天聊过什么、AI 答应过什么、它为什么这么排，全都没了。
/// 后果有两个，都不轻：
///   1. 界面上打开发现空空如也，昨天说的计划像从没发生过；
///   2. AI 每次都从零开始认识你——它不知道上次说好"今天先做第三章"，
///      于是每天重新开始劝，而"重复劝"正是会让人不想打开这个软件的事。
/// 决策 D9（对话上下文跨天延续）能不能成立，全靠这张表。
///
/// 【为什么继承 SyncEntityBase】
/// 以后手机端也要看到同一段对话（在电脑上聊的计划，手机上要能接着问）。
/// 带上 Version / UpdatedAt / DeletedAt 这套同步字段，
/// 到时候不用回来给这张表补迁移。
///
/// 【为什么只存 user 和 assistant 两种角色】
/// "连不上后端""撤回失败"这类系统提示不入库。它们是**这一次运行**的状态，
/// 不是对话内容；存下来会出现"每次打开都看到三条报错历史"，
/// 而那些报错早就过去了。
/// </summary>
public class ChatMessage : SyncEntityBase
{
    public const string RoleUser = "user";
    public const string RoleAssistant = "assistant";

    /// <summary>说话的一方：user 或 assistant。用常量而不是枚举，存进库里的是可读的字符串。</summary>
    public string Role { get; set; } = RoleUser;

    /// <summary>说的什么。</summary>
    public string Text { get; set; } = string.Empty;

    /// <summary>
    /// 这一轮 AI 改动的批次号。没改数据就为空。
    ///
    /// 【为什么必须存它】
    /// 撤回按钮靠它工作。需求 D4 定了"永久可撤"——
    /// 那就不只是本次运行能撤：今天改错了，明天打开也要能撤。
    /// 批次号不在库里，重开之后那个「撤回」就没了。
    /// </summary>
    public Guid? BatchId { get; set; }

    /// <summary>
    /// 这一轮改动了哪几条（JSON）。
    ///
    /// 【为什么要存，光有 BatchId 不够吗】
    /// 界面上要显示"撤回这 3 条改动"，还要列出具体改了什么。
    /// 这些明细不在 action_log 的摘要里（那边是给排查用的），
    /// 而重新算一遍又要回查，不如当时就存下来。
    /// 存的是那一轮的展示数据，不是事实来源——真正能撤的依据是 BatchId + action_log。
    /// </summary>
    public string? OutcomesJson { get; set; }

    /// <summary>
    /// 这一批是否已经撤回过。
    ///
    /// 【为什么要存】
    /// 撤过一次的批次不能再撤第二次（快照已经写回去了），
    /// 按钮会显示"已撤回"。这个状态不存的话，重开之后按钮又变回可点，
    /// 点下去什么都不会发生——"按钮能点但没反应"比没有按钮更让人困惑。
    /// </summary>
    public bool IsUndone { get; set; }
}
