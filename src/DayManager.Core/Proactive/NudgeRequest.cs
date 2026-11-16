namespace DayManager.Core.Proactive;

/// <summary>
/// 娱乐干预这一次要说的背景：**他正在哪个应用上、这一段刷了多久**。
///
/// 【为什么只传这两个字段，而不是把整份清单也传过来】
/// 清单、完成率、最近几天这些，后端自己查得到——它在库边上，客户端却在另一头。
/// 只有"现在屏幕上是什么、连着多久"这种**只有客户端在现场才知道**的事才需要传。
/// 少传一样，就少一处"两边各写一份、迟早不一致"的可能。
///
/// 【为什么放在 Core】
/// 客户端要引用它构造请求，后端要用它拼提示。两端共用同一个类型，
/// 改字段名时编译器会把两边一起叫出来，不会出现"改了一头忘了另一头"。
/// </summary>
/// <param name="AppName">应用显示名（"Google Chrome"）。写进提示，让 AI 知道他在干嘛。</param>
/// <param name="ContinuousMinutes">**这一段**连续用了多久。累计不要——理由见 <c>InterventionDecider</c>。</param>
public sealed record NudgeRequest(string AppName, int ContinuousMinutes);
