using CommunityToolkit.Mvvm.Messaging;
using DayManager.App.Messages;
using DayManager.App.Services;
using Microsoft.Maui.Graphics;
using MauiWindow = Microsoft.Maui.Controls.Window;

namespace DayManager.App.Platforms.Windows;

/// <summary>
/// 处理窗口最上面那条标题栏，让它在深色模式下不再是一条浅蓝白边。
///
/// 【这一小块为什么折腾了四种方案】
/// 上色这条路（AppWindow.TitleBar、DwmSetWindowAttribute、ExtendsContentIntoTitleBar）
/// 全都"返回成功、屏幕不动"。最后抓真实屏幕 + 逐层排查才定案：
/// MAUI 9 起自己接管标题栏绘制（自绘在客户区里），底色跟 **WinUI 层主题**，
/// 而我们选深色只改了 MAUI 层的 UserAppTheme，WinUI 层停在系统浅色。
/// 病根是主题没传导，不是窗口参数被覆盖——所以前面三种 API 全都无效。
/// 修法：把 WinUI 层主题一起掰过来（顺带滚动条等原生绘制也变深），
/// 再用 DWM 上色把底色精确对齐到页面背景。
/// </summary>
public static class WindowsTitleBar
{
    private static MauiWindow? _window;
    private static bool _hooked;

    /// <summary>
    /// 看着一个窗口，给它染标题栏，并在换主题时重染。
    /// </summary>
    public static void Attach(MauiWindow window)
    {
        _window = window;
        Diagnose("attach", $"Attach 被调用 ready={IsReady()}");

        // Handler 是"这个抽象的窗口"和"系统真正的那个窗口"之间的桥。
        // 窗口刚 new 出来的时候桥还没搭，PlatformView 是空的——
        // 这时候去系统里取 AppWindow 只能拿到 null，而且不报错。
        // 所以等 HandlerChanged 通知，桥一搭好立刻动手。
        if (IsReady())
        {
            ApplyCore();
            WatchThemeChanges();
            HookActivation();
            return;
        }

        window.HandlerChanged += OnHandlerChanged;
        window.Created += OnWindowCreated;
    }

    private static void OnHandlerChanged(object? sender, EventArgs e)
    {
        Diagnose("handler", $"HandlerChanged 触发 ready={IsReady()}");
        TryStart();
    }

    private static void OnWindowCreated(object? sender, EventArgs e)
    {
        Diagnose("created", $"Created 触发 ready={IsReady()}");
        TryStart();
    }

    private static void TryStart()
    {
        if (!IsReady())
        {
            return;
        }

        if (_window is not null)
        {
            _window.HandlerChanged -= OnHandlerChanged;
            _window.Created -= OnWindowCreated;
        }

        ApplyCore();
        WatchThemeChanges();
        HookActivation();
    }

    private static bool _activationHooked;

    /// <summary>
    /// 窗口第一次被激活后再补染一次。
    ///
    /// 【为什么创建时设了还要补】
    /// 实测（2026-10-29 凌晨）：在 CreateWindow 阶段把颜色写进 AppWindow.TitleBar，
    /// 窗口显示时系统会用主题默认色把它整个盖掉——不报错、读回值也正常，
    /// 就是屏幕上那条横条纹丝不动。这跟 UserAppTheme 会被建窗口动作盖掉是
    /// 同一个模式：<b>显示窗口这个动作本身会重写一遍窗口的外观参数</b>。
    /// 所以等 Activated（系统已经写完了）再写一次，这次才是最终生效的。
    /// </summary>
    private static void HookActivation()
    {
        if (_activationHooked || _window?.Handler?.PlatformView is not Microsoft.UI.Xaml.Window native)
        {
            return;
        }

        _activationHooked = true;
        native.Activated += (_, _) => ApplyCore();
    }

    private static bool IsReady() => _window?.Handler?.PlatformView is not null;

    /// <summary>
    /// 监听换主题的两种途径。
    ///
    /// 【为什么两样都听】
    /// <c>RequestedThemeChanged</c> 是系统层面的（用户在 Windows 设置里改了颜色），
    /// <c>ThemeChangedMessage</c> 是我们自己在时间页里选了深/浅档。
    /// 只听前者，用户在软件里自己切的时候标题栏不会跟着变，
    /// 剩下的那条白边会让人以为深色没生效。
    /// </summary>
    private static void WatchThemeChanges()
    {
        if (_hooked)
        {
            return;
        }

        _hooked = true;

        if (Application.Current is not null)
        {
            Application.Current.RequestedThemeChanged += (_, _) => ApplyCore();
        }

        WeakReferenceMessenger.Default.Register(_receiver,
            static (object _, ThemeChangedMessage _) => ApplyCore());
    }

    /// <summary>
    /// 收消息得有个收件人。用一个单例对象而不是 this——
    /// 这个类整个是静态的，没有一个实例能当身份。
    /// </summary>
    private static readonly object _receiver = new();

    private static void ApplyCore()
    {
        try
        {
            if (_window?.Handler?.PlatformView is not Microsoft.UI.Xaml.Window native)
            {
                Diagnose("early-exit", $"ApplyCore 早退: platformView={_window?.Handler?.PlatformView?.GetType().Name ?? "null"}");
                return;
            }

            // 【最终的真相】
            // 之前试过 AppWindow.TitleBar 上色、DwmSetWindowAttribute、
            // ExtendsContentIntoTitleBar——全部"返回成功、屏幕不动"。
            // 真正的原因：MAUI 9 起自己接管了标题栏的绘制（自绘在客户区里），
            // 它的底色跟的是 **WinUI 层的主题资源**。
            // 而我们选深色时改的是 MAUI 层的 UserAppTheme——
            // MAUI 的 AppThemeBinding 管内容区，WinUI 层的 ActualTheme 没人动，
            // 停在系统浅色上。于是内容深、标题栏浅，而且哪种窗口 API 都救不了它：
            // 不是"设置被覆盖"，是根本没病到那个部位。
            //
            // 修法就是把 WinUI 层的主题一起掰过来。
            // 顺带的收益：滚动条、悬停这些 WinUI 原生绘制的东西也一起变深。
            var theme = AppColors.IsDark
                ? Microsoft.UI.Xaml.ElementTheme.Dark
                : Microsoft.UI.Xaml.ElementTheme.Light;

            if (native.Content is Microsoft.UI.Xaml.FrameworkElement root)
            {
                root.RequestedTheme = theme;
            }

            // 【为什么这里还有 DWM 上色】
            // 主题掰正之后实测（外部先行验证）：DwmSetWindowAttribute 终于生效了——
            // 之前失败不是 API 错，是它管的那层标题栏色被浅色主题的绘制路径盖着，
            // 病根解除后它才轮得到说话。把它加上是为了精确对齐：
            // WinUI 深色主题给的标题栏底色偏蓝（#0A1428），跟页面 #1A1A1A 差一截，
            // 摆在一起仍看得出一条更蓝的横带。COLORREF 是 0x00BBGGRR，别按 RGBA 写。
            var handle = WinRT.Interop.WindowNative.GetWindowHandle(native);
            var caption = ToColorRef(AppColors.PageBg);
            var text = ToColorRef(AppColors.Text);
            DwmSetWindowAttribute(handle, 35, ref caption, sizeof(int)); // 标题栏底色
            DwmSetWindowAttribute(handle, 36, ref text, sizeof(int));    // 标题栏文字色
        }
        catch (Exception ex)
        {
            // 标题栏改不动不该挡住启动：最坏结果就是维持原来的样子。
            CrashLogger.Write("适配标题栏失败", ex);
        }
    }

    // COLORREF 是 0x00BBGGRR 字节序，跟 MAUI 的 RGBA 不同，转换别搞反。
    [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(nint hwnd, int attribute, ref int value, int size);

    private static int ToColorRef(Color c) =>
        ((byte)(c.Red * 255)) | (((byte)(c.Green * 255)) << 8) | (((byte)(c.Blue * 255)) << 16);

    private static void Diagnose(string tag, string message)
    {
        if (!_diagTags.Add(tag))
        {
            return;
        }

        CrashLogger.Write("标题栏诊断", new Exception($"[{tag}] {message}"));
    }

    private static readonly HashSet<string> _diagTags = new();
}
