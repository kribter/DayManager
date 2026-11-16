using CommunityToolkit.Mvvm.Messaging;
using DayManager.App.Messages;
using DayManager.App.ViewModels;

namespace DayManager.App.Controls;

/// <summary>
/// 把 <see cref="SelectableLabel"/> 接到原生控件的"文字可选"开关和右键菜单上。
///
/// 【为什么多这一个文件】
/// 选中从来不是 MAUI 自己的功能，而是原生控件的：
/// Windows 上是 WinUI TextBlock 的 IsTextSelectionEnabled，Android 上是 TextView 的 SetTextIsSelectable。
/// 两边类型完全不同，只能在 platform 分支里各写一行。
///
/// 【为什么右键菜单也在这里】
/// 试过 MAUI 的 ContextFlyout 附加属性——XAML 编译通过，Windows 上右键却弹不出来
/// （气泡这条 handler 链路没有把它接到原生控件上）。
/// 与其猜映射，不如直接给原生 TextBlock 挂 WinUI 的 MenuFlyout：
/// WinUI 的规矩是 UIElement 右键自动弹它自己的 ContextFlyout，这一步绕不开也不需要绕。
/// 菜单项只发一条消息（DeleteChatBubbleMessage），删除的规则仍在 ViewModel。
///
/// 【Android 上还没有右键菜单】
/// TextView 开了 SetTextIsSelectable 后长按自带选择工具栏；
/// 删除菜单等 Android 迁移（M10）时再补。
/// </summary>
public static class SelectableLabelMapper
{
    private const string Key = nameof(SelectableLabel) + "TextSelection";

    private static bool _registered;

    public static void Register()
    {
        // 多次 Build() 会重复注册，同一个 key 的 mapping 会被跑两遍——虽然没害处，但没必要。
        if (_registered)
        {
            return;
        }

        _registered = true;

        Microsoft.Maui.Handlers.LabelHandler.Mapper.AppendToMapping(Key, (handler, view) =>
        {
            if (view is not SelectableLabel)
            {
                return;
            }

            EnableSelection(handler);
        });
    }

    private static void EnableSelection(Microsoft.Maui.Handlers.ILabelHandler handler)
    {
#if WINDOWS
        if (handler.PlatformView is not Microsoft.UI.Xaml.Controls.TextBlock text)
        {
            return;
        }

        text.IsTextSelectionEnabled = true;

        if (handler.VirtualView is SelectableLabel label)
        {
            AttachContextMenu(label, text);
        }
#elif ANDROID
        if (handler.PlatformView is global::Android.Widget.TextView text)
        {
            text.SetTextIsSelectable(true);
        }
#endif
    }

#if WINDOWS
    /// <summary>
    /// 给气泡文字挂上右键菜单。每次映射建一个新的 MenuFlyout，
    /// 菜单项通过 Tag 拿回控件、再从控件的 BindingContext 拿到那句话——
    /// 右键那一刻 BindingContext 一定已经就位，不用关心映射时它存不存在。
    /// </summary>
    private static void AttachContextMenu(SelectableLabel label, Microsoft.UI.Xaml.Controls.TextBlock text)
    {
        var item = new Microsoft.UI.Xaml.Controls.MenuFlyoutItem { Text = "删除这一句" };
        item.Tag = label;
        item.Click += OnDeleteClicked;

        var menu = new Microsoft.UI.Xaml.Controls.MenuFlyout();
        menu.Items.Add(item);

        text.ContextFlyout = menu;
    }

    private static void OnDeleteClicked(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        if (sender is Microsoft.UI.Xaml.Controls.MenuFlyoutItem { Tag: SelectableLabel label }
            && label.BindingContext is ChatBubble { IsSystem: false } bubble)
        {
            // 系统提示不进菜单（它不是对话内容，是一时的故障说明）；
            // 这里防御一次，防止哪天有人把菜单挂到它头上。
            WeakReferenceMessenger.Default.Send(new DeleteChatBubbleMessage(bubble));
        }
    }
#endif
}
