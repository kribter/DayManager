using DayManager.App.ViewModels;

namespace DayManager.App.Messages;

/// <summary>
/// 右键菜单「删除这一句」的转送信封。
///
/// 【为什么要这一个中转】
/// 右键菜单挂在原生层（SelectableLabelMapper）：那一层拿到的只有控件，
/// 而删除这件事的规则在 ChatViewModel 里（软删、还没落库的直接从界面拿掉）。
/// 平台代码不该反过来引用页面，就让消息走项目里现成的 Messenger：
/// 原生层只负责"用户想删这一条"，怎么做由 ViewModel 决定。
/// </summary>
public sealed class DeleteChatBubbleMessage
{
    public DeleteChatBubbleMessage(ChatBubble bubble)
    {
        Bubble = bubble;
    }

    /// <summary>用户对着它按了右键的那条话。</summary>
    public ChatBubble Bubble { get; }
}
