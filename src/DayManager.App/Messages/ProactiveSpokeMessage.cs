namespace DayManager.App.Messages;

/// <summary>
/// AI 主动说了一句话（已经写进对话记录）。
///
/// 【为什么要有独立的消息，而不是复用 ItemsChangedMessage】
/// 那一条是"数据变了，重新加载"的意思，订阅它的页面（日程、大任务）
/// 会去重新查库。主动开口改的是**对话**，让日程页为此白查一次库没意义。
/// 分开之后，只有对话页会响应。
/// </summary>
public sealed record ProactiveSpokeMessage;
