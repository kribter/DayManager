using System.Collections.Generic;
using System.Linq;
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

    private static void OnHandlerChanged(object? sender, EventArgs e) => TryStart();

    private static void OnWindowCreated(object? sender, EventArgs e) => TryStart();

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
                // 【直接给客户区自绘的横条上色】
                // AppWindow.TitleBar 那套颜色参数在 ExtendsContentIntoTitleBar=true 时
                // 全部被忽略（2026-10-29 实测：值写进去、屏幕不动、无报错）——
                // 因为标题条根本不是非客户区，是 MAUI 的 WindowRootView 在客户区里画的。
                // 所以真正有效的做法是钻进可视树，把画横条的元素挨个找到：
                //   - 窗口根布局的 Background（标题条区域的底，透明时露出来的就是它）
                //   - 名叫 AppTitle 的 TextBlock（左上角"DayManager"那行字）
                //   - NavigationView 的 TopNavArea（顶部标签行那条横带）
                // 这些元素 MAUI 没给公开的设置口子，只能这样按名字找。
                SetRootBackground(root);
                if (!_shellTinted)
                {
                    // 【为什么挂 LayoutUpdated 反复试】
                    // 可视树是分层长出来的：Handler 好了 ≠ Shell 的模板应用完了 ≠
                    // 内层 NavigationView 的 TopNavArea 存在。Loaded 只保证根布局加载，
                    // 实测（2026-10-29）赶不上内层模板，标签行还是黑的。
                    // LayoutUpdated 每次布局都会发，涂上 TopNavArea 之前每次都再试，
                    // 成功（_shellTinted）就摘掉，不留常驻开销。
                    if (_layoutHookedRoot is null)
                    {
                        _layoutHookedRoot = root;
                        root.LayoutUpdated += OnRootLayoutUpdated;
                    }
                    TintShell(root);
                }
            }

            // 【为什么这里还有 DWM 上色】
            // 主题掰正之后实测（外部先行验证）：DwmSetWindowAttribute 终于生效了——
            // 之前失败不是 API 错，是它管的那层标题栏色被浅色主题的绘制路径盖着，
            // 病根解除后它才轮得到说话。把它加上是为了精确对齐：
            // WinUI 深色主题给的标题栏底色偏蓝（#0A1428），跟页面 #1A1A1A 差一截，
            // 摆在一起仍看得出一条更蓝的横带。COLORREF 是 0x00BBGGRR，别按 RGBA 写。
            // 【标题条本身再精确上色一次】
            // 主题掰正之后 AppWindow.TitleBar 的直接上色也轮得到生效了
            // （之前失败同样是主题病根盖着它）。DWM 那两行留着当兜底，
            // 两者目标是同一个颜色，谁先画都一样。
            // 注意 ButtonForegroundColor 也要设，否则深色条上那三个
            // 最小化/最大化/关闭按钮还是按浅色主题画的深灰箭头，看不清。
            var handle = WinRT.Interop.WindowNative.GetWindowHandle(native);

            // 主题掰过之后齿轮的笔画也要跟着换色。
            // EnsureSettingsButton 里之所以不做这件事，是因为它只在找到 TopNavArea 时被调到，
            // 而用户切主题时不一定再走那一步——这里补一次才跟得上。
            if (_settingsButton is not null)
            {
                _settingsButton.Foreground = Brush(AppColors.Muted);
            }

            var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(handle);
            if (Microsoft.UI.Windowing.AppWindow.GetFromWindowId(windowId) is { } appWindow)
            {
                var titleBar = appWindow.TitleBar;
                titleBar.BackgroundColor = ToWinColor(AppColors.PageBg);
                titleBar.ForegroundColor = ToWinColor(AppColors.Text);
                titleBar.ButtonForegroundColor = ToWinColor(AppColors.Text);
                titleBar.ButtonHoverForegroundColor = ToWinColor(AppColors.Text);
                titleBar.InactiveBackgroundColor = ToWinColor(AppColors.PageBg);
                titleBar.InactiveForegroundColor = ToWinColor(AppColors.Faint);
                titleBar.ButtonInactiveForegroundColor = ToWinColor(AppColors.Faint);
            }

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

    private static global::Windows.UI.Color ToWinColor(Color c) =>
        new() { A = 255, R = (byte)(c.Red * 255), G = (byte)(c.Green * 255), B = (byte)(c.Blue * 255) };

    private static Microsoft.UI.Xaml.Media.SolidColorBrush Brush(Color c) => new(ToWinColor(c));

    /// <summary>窗口根布局的底色。Background 在 Panel/Control 上，FrameworkElement 本身没有。</summary>
    private static void SetRootBackground(Microsoft.UI.Xaml.FrameworkElement root)
    {
        switch (root)
        {
            case Microsoft.UI.Xaml.Controls.Panel panel:
                panel.Background = Brush(AppColors.PageBg);
                break;
            case Microsoft.UI.Xaml.Controls.Control control:
                control.Background = Brush(AppColors.PageBg);
                break;
        }
    }

    private static Microsoft.UI.Xaml.FrameworkElement? _layoutHookedRoot;
    private static bool _shellTinted;
    private static readonly List<Microsoft.UI.Xaml.Controls.StackPanel> _tintedAreas = new();

    private static void OnRootLayoutUpdated(object? sender, object e)
    {
        // 已经涂过的 TopNavArea 每次布局都复查一遍：模板重应用、显示模式切换
        // 都可能造出新实例或把值盖回去。发现被盖就重涂并记日志。
        foreach (var sp in _tintedAreas)
        {
            var b = sp.Background as Microsoft.UI.Xaml.Media.SolidColorBrush;
            if (b is null || b.Color.R != 0x1A || b.Color.G != 0x1A || b.Color.B != 0x1A)
            {
                CrashLogger.Write("标题栏诊断", new Exception(
                    $"[topnav] overwritten bg={DescribeBrush(sp.Background)}, re-tinting"));
                sp.Background = Brush(AppColors.PageBg);
            }

            // 每次复查都顺手确认齿轮还挂在树上：
            // 上面那条日志说的"模板重应用会换掉元素实例"，对按钮一样成立——
            // 被摘掉之后不重新挂上去，设置入口就凭空消失了，而且没有任何报错。
            // 顺手把这一小片也重新涂一遍（TintShell 会走到 TopNavGrid 分支，
            // EnsureSettingsButton 里面有节流，重复调没有代价）。
            TintShell(sp);
        }

        if (_shellTinted)
        {
            return;
        }

        if (_window?.Handler?.PlatformView is Microsoft.UI.Xaml.Window w &&
            w.Content is Microsoft.UI.Xaml.FrameworkElement r)
        {
            SetRootBackground(r);
            TintShell(r);
        }
    }

    // ---------- 右上角的设置齿轮 ----------

    private static Microsoft.UI.Xaml.Controls.Button? _settingsButton;

    /// <summary>
    /// 把齿轮挂进标签行（TopNavGrid）右端。重复调用是安全的——挂好了就不再动。
    ///
    /// 【为什么齿轮要这样做，而不是在界面上加一个按钮】
    /// Shell 的顶部是 WinUI 的 NavigationView 画出来的，几个 Tab 之外全是原生控件，
    /// 页面上画的东西都落在标签行下面，想放进那一行只能走可视树这一条路。
    /// 好处是它不占额外的高度——用户当初关掉标题栏就是为了把那一行还给清单。
    /// 挂进标签行容器之后右对齐、垂直居中，天然和标签文字齐平，
    /// 不需要任何"往下挪多少像素"的测量——那种测量在这个结构里测不准（实测）。
    ///
    /// 【为什么挂载必须异步，哪怕调用时已经在 UI 线程上】
    /// 这个方法的调用方之一是 LayoutUpdated——**布局回调**。
    /// 在布局回调里同步改可视树（Children.Add），WinUI 会报
    /// LayoutCycleException（2026-10-30 实测，整个窗口黑掉）：
    /// "我这轮布局还没算完，你又塞了个新元素进来逼我重新布局"。
    /// 所以挂载一律 Dispatch 到下一拍，脱离布局回调的调用栈再动手。
    ///
    /// 【为什么挂上就不再搬家】
    /// Shell 是两层 NavigationView，可能碰上一条以上的 TopNavGrid；
    /// "从旧的摘下来挂到新的"是个竞态：两个 Dispatch 闭包谁后执行看运气。
    /// 挂上第一条就认它——每条标签行容器都是满宽的，挂哪条贴的都是右边缘。
    ///
    /// 【为什么要节流】
    /// LayoutUpdated 每帧都发。不做节流的话，这里每帧都会排一次 Dispatch，
    /// 而 Dispatch 本身又可能催生下一轮布局——恶性循环。
    /// 半秒最多试一次；成功挂好之后，闭包里发现按钮已经在位就什么都不做。
    ///
    /// 【为什么数失败次数】
    /// 挂载永远失败的话（比如某个系统版本不接受这种塞法），
    /// 就该安静地放弃：少一个设置入口是可用性损失，不是崩溃。
    /// 三次不成先认输，等下次主题切换还会再给机会。
    /// </summary>
    private static void EnsureSettingsButton(Microsoft.UI.Xaml.Controls.Grid? navGrid)
    {
        var now = Environment.TickCount64;

        if (now - _lastSettingsAttempt < 500 || _settingsFailures >= MaxSettingsFailures)
        {
            return;
        }

        _lastSettingsAttempt = now;

        Microsoft.Maui.Controls.Application.Current?.Dispatcher.Dispatch(() =>
        {
            try
            {
                var button = _settingsButton ??= BuildSettingsButton();

                if (button.Parent is not null)
                {
                    // 已经挂上了：只把颜色跟紧主题，不搬家。
                    button.Foreground = Brush(AppColors.Muted);
                    _settingsFailures = 0;
                    return;
                }

                // 【为什么挂标签行容器的父 Grid，而不是 TopNavGrid 自己】
                // TopNavGrid 里面是列布局，新塞进来的按钮没有列号，会被归进第 0 列，
                // Right 对齐成了"第 0 列的右端"——实际落在容器左边界外面（实测 X=-34），
                // 看起来就是"挂了但看不见"；用 Translation 硬推又被整个无视（实测无效）。
                // 它的父 Grid 是普通的叠放容器（TopNavArea、SplitView 全都叠在一个格里），
                // Right 对齐在这种 Grid 里说到做到。按钮 Top 对齐 + 8px 边距，
                // 正好落在标签行的行高里，和文字齐平。
                var host = FindTabRowHost();

                if (host is null)
                {
                    // 这一刻没有活着的容器（多半是启动太早，模板还没展开）。
                    // 什么都不挂，等下一轮复查带着新几何再来。
                    if (!_loggedNoHost)
                    {
                        _loggedNoHost = true;
                        CrashLogger.Write("齿轮诊断", new Exception("no live tab-row host found"));
                    }

                    return;
                }

                host.Children.Add(button);
                // 【为什么提 z 序】host 里后加的 SplitView 等大件会按叠放顺序画在
                // 同层；按钮虽然也是后加，但实测仍被标签行的原生内容压住
                // （红块实验：只有行底部一条缝露出来）。ZIndex 直接拉满。
                Microsoft.UI.Xaml.Controls.Canvas.SetZIndex(button, 1000);
                button.Foreground = Brush(AppColors.Muted);
                _settingsFailures = 0;
            }
            catch (Exception ex)
            {
                // 失败的半成品不能留着力气下次接着用——
                // 复用一个挂不上去的按钮只会一直失败。丢掉重造。
                _settingsButton = null;
                _settingsFailures++;
                if (_settingsFailures <= MaxSettingsFailures)
                {
                    // 齿轮挂不上只是少了一个入口，不该把整个标题栏的适配拖崩。
                    CrashLogger.Write("挂设置齿轮失败", ex);
                }
            }
        });
    }

    /// <summary>挂载齿轮的连续失败上限，超过就先不再试（见上面注释）。</summary>
    private const int MaxSettingsFailures = 3;

    private static int _settingsFailures;

    /// <summary>上次尝试挂载的时刻（TickCount 毫秒）。节流用，跨线程读它没有 COM 风险。</summary>
    private static long _lastSettingsAttempt;

    /// <summary>"找不到活容器"只记一次日志，别让高频重试把它刷成瀑布。</summary>
    private static bool _loggedNoHost;

    /// <summary>
    /// 从窗口当前的内容树里找"正在显示"的标签行所在的那个 Grid。
    ///
    /// 【为什么绕了两层】
    /// 树里叫 TopNavGrid 的容器可能不止一个：外层 NavigationView 是 Left 模式，
    /// 它自己的 TopNavGrid 恒为 0×0（永不显示）；真正渲染标签行的是
    /// SplitView 深处内层 NavigationView 的那一条。第一个碰到的是外层的——
    /// 直接挂就是"成功挂载、永远不可见"，连报错都没有（2026-10-30 实测）。
    /// 所以按 ActualWidth 挑出有尺寸的那条 TopNavGrid，再取它的父 Grid
    /// （一个普通的叠放容器）作为挂靠点——Right 对齐在那里说到做到。
    /// </summary>
    private static Microsoft.UI.Xaml.Controls.Grid? FindTabRowHost()
    {
        if (_window?.Handler?.PlatformView is not Microsoft.UI.Xaml.Window w ||
            w.Content is not Microsoft.UI.Xaml.FrameworkElement root)
        {
            return null;
        }

        Microsoft.UI.Xaml.Controls.Grid? widest = null;
        FindWidestTopNavGrid(root, ref widest);

        if (widest?.Parent is Microsoft.UI.Xaml.Controls.StackPanel navArea &&
            navArea.Parent is Microsoft.UI.Xaml.Controls.Grid host &&
            host.XamlRoot is not null)
        {
            return host;
        }

        return null;
    }

    private static void FindWidestTopNavGrid(Microsoft.UI.Xaml.DependencyObject node, ref Microsoft.UI.Xaml.Controls.Grid? best)
    {
        if (node is Microsoft.UI.Xaml.Controls.Grid g &&
            g.Name == "TopNavGrid" &&
            g.XamlRoot is not null &&
            (best is null || g.ActualWidth > best.ActualWidth))
        {
            best = g;
        }

        var count = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(node);
        for (var i = 0; i < count; i++)
        {
            FindWidestTopNavGrid(Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(node, i), ref best);
        }
    }

    private static Microsoft.UI.Xaml.Controls.Button BuildSettingsButton()
    {
        var button = new Microsoft.UI.Xaml.Controls.Button
        {
            // Segoe Fluent Icons（Win11）里的「设置」字形，Win10 上有同名同位的 MDL2 兜底。
            Content = "\uE713",
            FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
            FontSize = 16,
            Width = 34,
            Height = 32,
            Padding = new Microsoft.UI.Xaml.Thickness(0),
            Margin = new Microsoft.UI.Xaml.Thickness(0, 8, 8, 0),
            HorizontalAlignment = Microsoft.UI.Xaml.HorizontalAlignment.Right,
            VerticalAlignment = Microsoft.UI.Xaml.VerticalAlignment.Top,
            HorizontalContentAlignment = Microsoft.UI.Xaml.HorizontalAlignment.Center,
            VerticalContentAlignment = Microsoft.UI.Xaml.VerticalAlignment.Center,
            // 透明底：标签行已经染成了页面色，再画一层按钮底色就会露出一个方块。
            Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(new global::Windows.UI.Color { A = 0 }),
            BorderThickness = new Microsoft.UI.Xaml.Thickness(0)
        };

        Microsoft.UI.Xaml.Controls.ToolTipService.SetToolTip(button, "设置");
        button.Click += OnSettingsClicked;
        return button;
    }

    private static void OnSettingsClicked(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        try
        {
            // 【为什么不能直接在这儿导航】
            // Click 是 WinUI 的 UI 线程回调，而 Shell 的导航得走 MAUI 的调度器。
            // 窗口还藏在托盘里时也要先叫回来——和托盘菜单里那段的处理一致：
            // 先 Restore，再压进设置页，人才看得见发生了什么。
            Microsoft.Maui.Controls.Application.Current?.Dispatcher.Dispatch(() =>
            {
                _ = Microsoft.Maui.Controls.Shell.Current.GoToAsync("SettingsPage");
            });
        }
        catch (Exception ex)
        {
            CrashLogger.Write("设置齿轮跳转失败", ex);
        }
    }

    /// <summary>
    /// 遍历可视树，给 Shell 的原生绘制部件上色。
    ///
    /// 【为什么按名字找而不是走公开 API】
    /// Shell 在 Windows 上是两层 NavigationView（外层 RootNavigationView 套内层顶部标签栏），
    /// 顶部标签那条横带的底色是模板里 TopNavArea 的 {ThemeResource NavigationViewTopPaneBackground}，
    /// MAUI 没有暴露任何属性能改它。应用级 ThemeDictionaries 覆盖够不到模板；
    /// 直接赋值的元素实例又可能被模板重应用换掉——所以两条都做：
    /// 元素资源塞覆盖（重应用后新实例会解析到我们的色）+ 直接赋值（当下这帧就对）。
    /// 找不到就跳过：结构变化时宁可退回系统默认，不能崩。
    /// </summary>
    private static void TintShell(Microsoft.UI.Xaml.DependencyObject node)
    {
        switch (node)
        {
            case Microsoft.UI.Xaml.Controls.NavigationView nav
                when node is Microsoft.UI.Xaml.FrameworkElement navElement:
                // 给 NavigationView 自身资源塞一份覆盖：标签项文字色是
                // ShellItemHandler 从这里读的（MAUI 源码里就是这么取的）；
                // 顶栏底色资源是给模板重应用时的新 TopNavArea 兜底——
                // 直接赋值的元素实例会被换掉，元素资源不会。
                navElement.Resources["NavigationViewItemForeground"] = Brush(AppColors.Muted);
                navElement.Resources["NavigationViewTopPaneBackground"] = Brush(AppColors.PageBg);
                break;
            case Microsoft.UI.Xaml.Controls.TextBlock tb when tb.Name == "AppTitle":
                tb.Foreground = Brush(AppColors.Text);
                break;
            case Microsoft.UI.Xaml.Controls.StackPanel sp when sp.Name == "TopNavArea":
                sp.Background = Brush(AppColors.PageBg);
                _shellTinted = true;
                if (!_tintedAreas.Contains(sp))
                {
                    _tintedAreas.Add(sp);
                }
                break;
            case Microsoft.UI.Xaml.Controls.Grid g when g.Name == "TopNavGrid":
                // 齿轮的家。TopNavGrid 是标签行本体（里面装着 tabs 的 ItemsRepeater），
                // 挂这里不用任何偏移计算：右对齐贴边、垂直居中，天然和标签文字齐平。
                //
                // 【为什么当初放弃了它】第一次诊断它 ActualWidth=0，以为是隐藏容器——
                // 其实只是读得太早、布局还没跑。标签正常显示这件事本身就证明它有尺寸。
                // 绕道"挂 TopNavArea + 渲染偏移"折腾了四轮（COM 异常、布局循环、
                // 两条带抢按钮、TransformToVisual 报假坐标），最后回到这里一步到位。
                EnsureSettingsButton(g);
                break;
        }

        var count = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(node);
        for (var i = 0; i < count; i++)
        {
            TintShell(Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(node, i));
        }
    }

    private static string DescribeBrush(Microsoft.UI.Xaml.Media.Brush? b) =>
        b is Microsoft.UI.Xaml.Media.SolidColorBrush s ? $"#{s.Color.R:X2}{s.Color.G:X2}{s.Color.B:X2}" : b?.GetType().Name ?? "null";
}
