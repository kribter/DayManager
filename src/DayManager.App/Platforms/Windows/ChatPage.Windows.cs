using Microsoft.UI.Input;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace DayManager.App;

/// <summary>
/// 对话页在 Windows 上的那部分：把回车接到发送上。
///
/// 【为什么这个文件要单独放】
/// MAUI 的 Editor 是一个"多行输入框"，它在各平台上的原生控件完全不是一回事：
/// Windows 是 WinUI 的 TextBox，Android 是 EditText + ImeOptions。
/// "按回车做什么"这件事只能在原生那一层拦，没有跨平台写法。
/// 放进平台目录，MAUI 编译 Windows 版本时才带上它，Android 打包时不会碰到 WinUI 的类型。
///
/// 【为什么拦 PreviewKeyDown 而不是 KeyDown】
/// KeyDown 是控件**已经处理过**之后才轮到的：等到那一刻，换行符已经插进输入框了，
/// 再拦就晚了——你会看到发出去的消息前面多一个空行。
/// PreviewKeyDown 在控件之前，把它标成 Handled，这一下就当没按过。
/// </summary>
public partial class ChatPage
{
    /// <summary>已经接过一次就别再接——Handler 重建会再次触发事件，重挂会变成按一次发两条。</summary>
    private bool _enterHooked;

    /// <summary>
    /// 输入法是不是正在组字。
    ///
    /// 【为什么必须有这个判断】
    /// 打中文时，拼音打完、候选词还没上屏的那段过程，Enter 的意思是"就这个词"，
    /// 不是"发送"。如果这时候把消息发出去，打一半的字连同候选框一起没了，
    /// 而且发出去的是一句谁也看不懂的半成品。
    /// 这不是边角情况——这个软件的主要输入方式就是中文。
    /// </summary>
    private bool _composing;

    partial void AttachEnterToSend()
    {
        Hookup(InputEditor);

        // 页面构造的时候，原生控件往往还没建出来（Handler 是延迟创建的），
        // 建好之后会发这个事件，那时再接一次。
        InputEditor.HandlerChanged += (_, _) => Hookup(InputEditor);
    }

    private void Hookup(Editor editor)
    {
        if (_enterHooked || editor.Handler?.PlatformView is not TextBox box)
        {
            return;
        }

        _enterHooked = true;

        box.TextCompositionStarted += (_, _) => _composing = true;
        box.TextCompositionEnded += (_, _) => _composing = false;

        box.PreviewKeyDown += OnPreviewKeyDown;
    }

    private void OnPreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Enter)
        {
            return;
        }

        // 输入法正在组字：这一下 Enter 归它，不是发给 AI 的。
        if (_composing)
        {
            return;
        }

        // 按住 Shift 就是"我要换行"——照原样交给输入框，什么都不做。
        var shift = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift);
        if (shift.HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down))
        {
            return;
        }

        e.Handled = true;

        // 空的就不发（ViewModel 里也会挡一次，这里挡是为了不白跑一趟）。
        if (string.IsNullOrWhiteSpace(_viewModel.InputText))
        {
            return;
        }

        _viewModel.SendCommand.Execute(null);
    }
}
