namespace DayManager.Core.Proactive;

/// <summary>
/// 专注计时到点那句收尾话的背景：**他刚把哪一件事做了多少分钟**。
///
/// 【为什么要单独一个类型，而不是复用 NudgeRequest】
/// 两个场合虽然都只传"名字 + 分钟数"，但名字的语义完全不同：
/// nudge 传的是**应用名**（他在刷什么），focus 传的是**日程标题**（他做了什么）。
/// 复用会让提示词里出现"他正在 XX 上刷了 N 分钟"这种错句——
/// 那种错误不报错，只是每次说话都说得不对。
/// 两个 record 各占十行，换来的是改字段名时编译器会同时叫出两边。
///
/// 【为什么不叫 FocusRequest】
/// Microsoft.Maui 里已经有 FocusRequest（键盘焦点用），撞名会让客户端每个引用点
/// 都得写全名。多一个 Finish，读起来也更像"跑完一轮"这件事。
/// </summary>
/// <param name="ItemTitle">这一轮做的那件事的标题。</param>
/// <param name="Minutes">这一轮计时的长度（通常是 10）。</param>
public sealed record FocusFinishRequest(string ItemTitle, int Minutes);
