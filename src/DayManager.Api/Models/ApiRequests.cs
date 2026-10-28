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
}

/// <summary>
/// 让 AI 主动说一句话（没有人提问）。
/// </summary>
public sealed class ProactiveRequest
{
    /// <summary>
    /// 场合：morning / before / evening / lowrate。
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
