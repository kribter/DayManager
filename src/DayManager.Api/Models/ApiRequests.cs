namespace DayManager.Api.Models;

/// <summary>跟"会动手的 AI"说一句话。</summary>
public sealed class AgentChatRequest
{
    public List<ChatMessage>? Messages { get; set; }

    /// <summary>
    /// "今天"是哪一天（yyyy-MM-dd）。可空，默认服务端当天。
    ///
    /// 【为什么要让客户端能指定】
    /// 客户端可能正停在"明天"那一页跟 AI 说话，
    /// 那时 AI 眼里的"今天"必须是用户正在看的那一天，
    /// 否则会出现"我说明天的事，它却去改了今天"的错位。
    /// </summary>
    public DateOnly? Today { get; set; }

    /// <summary>本次请求自带的密钥 / 接口地址 / 模型。见 <see cref="AiOverride"/> 的说明。</summary>
    public AiOverride? Ai { get; set; }
}

/// <summary>
/// 让 AI 主动说一句话（没有人提问）。
/// </summary>
public sealed class ProactiveRequest
{
    /// <summary>
    /// 场合：morning / before / evening / lowrate / nudge / focus。
    ///
    /// 【为什么用字符串而不是枚举】
    /// 这个值要跨进程传，用字符串在网络上和日志里都一眼可读；
    /// 用枚举传的是数字，抓包看到 "3" 还得回去查表。
    /// </summary>
    public string Kind { get; set; } = "morning";

    /// <summary>场合为 before（日程前提醒）时，是那一条的 id。</summary>
    public Guid? ItemId { get; set; }

    /// <summary>"今天"是哪一天（yyyy-MM-dd）。可空，默认服务端当天。</summary>
    public DateOnly? Today { get; set; }

    /// <summary>
    /// 场合为 nudge（娱乐超时干预）时的现场：他正在刷哪个应用。
    ///
    /// 【为什么这两个字段只有 nudge 用得上，却放在这个共用请求里】
    /// 它们描述的是"客户端此刻看到的事"——后端是另一个进程，它守着数据库，
    /// 但看不见屏幕。除了这两样之外 AI 需要的一切后端都能自己查，
    /// 所以只传这两样：多传一样，就多一处两边各写一份、迟早不一致的地方。
    /// </summary>
    public string? AppName { get; set; }

    /// <summary>场合为 nudge 时，**这一段**连续用了多少分钟。</summary>
    public int? ContinuousMinutes { get; set; }

    /// <summary>
    /// 场合为 focus（专注到点）时的现场：他刚做的那件事叫什么。
    /// 同样是"客户端在现场才知道"的信息——后端看得到日程表，
    /// 但不知道他刚才对着哪一条坐了十分钟。
    /// </summary>
    public string? FocusTitle { get; set; }

    /// <summary>场合为 focus 时，这一轮计时有多长（分钟）。</summary>
    public int? FocusMinutes { get; set; }

    /// <summary>本次请求自带的密钥 / 接口地址 / 模型。见 <see cref="AiOverride"/> 的说明。</summary>
    public AiOverride? Ai { get; set; }
}

/// <summary>手工新建一条（不经 AI）。</summary>
public sealed class CreateItemRequest
{
    public string Title { get; set; } = string.Empty;
    public string? Date { get; set; }
    public string? Time { get; set; }
}

/// <summary>手工改状态（不经 AI）。</summary>
public sealed class SetStatusRequest
{
    public string? Status { get; set; }

    /// <summary>跳过的原因。只在 status=skipped 时有意义。</summary>
    public string? Reason { get; set; }
}

/// <summary>改一条 AI 记忆（用户纠正）。</summary>
public sealed class UpdateMemoryRequest
{
    public string? Content { get; set; }
    public string? Reason { get; set; }
}

/// <summary>手动触发记忆提炼（筛最近 N 天）。</summary>
public sealed class DistillRequest
{
    public int? Days { get; set; }
    public AiOverride? Ai { get; set; }
}
