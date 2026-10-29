using System.Runtime.InteropServices;
using DayManager.App.Services;
using Microsoft.UI.Windowing;
using WinRT.Interop;

namespace DayManager.App.Platforms.Windows;

/// <summary>
/// 把窗口藏到托盘，程序照常采集、照常提醒。
///
/// 【为什么必须有这个东西】
/// 使用时长是每 5 秒采一次前台窗口的，干预引擎也每分钟轮询一次——两者都活在窗口背后。
/// 关掉界面就把它们一起关了，等于全天数据断档：晚上打开一看，今天只记住了那几次"打开过软件"。
/// 而这个软件要干预的恰恰是"打开电脑就刷 B 站"——那时候人根本不会特意打开它。
///
/// 所以默认做法是：点 X 只是把窗口收起来，任务栏也不占位置，托盘上留一个图标；
/// 想要真正退出，从托盘菜单里选。
///
/// 【为什么用 Win32 的 Shell_NotifyIcon，而不是找个库】
/// MAUI 和 WinUI 都没有托盘 API（这是 Windows 桌面十几年的老话题了），
/// System.Windows.Forms 的 NotifyIcon 则要为此引入整套 WinForms——
/// 在一个 WinUI 项目里塞 WinForms，等于为了让一个人住进来重新盖一栋楼。
/// 直接 P/Invoke 一百多行就把事办了，且它能做出最像 Windows 原生的右键菜单。
/// </summary>
public sealed class WindowsTray : IWindowShell, IDisposable
{
    // ---- 托盘图标 ----

    private const int NimAdd = 0x00000000;
    private const int NimModify = 0x00000001;
    private const int NimDelete = 0x00000002;

    private const int NifMessage = 0x00000001;
    private const int NifIcon = 0x00000002;
    private const int NifTip = 0x00000004;
    private const int NifInfo = 0x00000010;

    // ---- 鼠标与通知 ----

    private const int WmLButtonUp = 0x0202;
    private const int WmRButtonUp = 0x0205;

    /// <summary>点了气泡提示本体。</summary>
    private const int NinBalloonUserClick = 0x0405;

    private const int WmCommand = 0x0111;

    /// <summary>托盘回调的消息号。必须 ≥ WM_APP，否则会和系统自己的窗口消息撞车。</summary>
    private const int TrayCallback = 0x8001;

    private const int MenuOpen = 1001;
    private const int MenuQuit = 1002;
    private const int MenuSettings = 1003;

    /// <summary>explorer 重启后会广播这个消息。不重新加图标的话，托盘上就永远空了。</summary>
    private const string TaskbarCreatedName = "TaskbarCreated";

    /// <summary>
    /// 托盘回调需要一个窗口来收消息，而这个窗口谁也不该看见。
    ///
    /// 【为什么另外建一个窗口，而不是拿主窗口自己收】
    /// 主窗口会被藏起来、会被重建，而收托盘消息的句柄必须一直活着。
    /// HWND_MESSAGE 建立的"只收消息的窗口"不参与显示，正合适。
    /// </summary>
    private IntPtr _hostWindow;

    private IntPtr _icon;
    private bool _iconAdded;
    private uint _taskbarCreatedMessage;

    private AppWindow? _appWindow;
    private IntPtr _mainWindowHandle;

    /// <summary>点 X 时是"藏起来"还是"真退出"。默认藏着，由界面设置可以改回来。</summary>
    private bool _closeToTray = true;

    private bool _disposed;

    /// <summary>这一次是不是"开机自己起来的，别露面"。</summary>
    private bool _startHidden;

    /// <summary>第一次被 X 掉时要弹气泡说明去了哪里，之后就安静——反复提示会被嫌烦。</summary>
    private bool _hasExplainedBalloon;

    /// <summary>
    /// 窗口过程有一份静态实例。
    ///
    /// 【为什么是静态的】
    /// Win32 的窗口过程是一个函数指针，而 C# 的委托一旦被回收，
    /// 系统那边握着的指针就悬空了——下一次来消息直接崩在 native 里，连堆栈都没有。
    /// 所以委托必须存在静态字段里，活得跟进程一样长。
    /// 整个程序只有一个托盘宿主，这份 singleton 是够用的。
    /// </summary>
    private static readonly NativeMethods.WndProc Proc = TrayWndProc;
    private static WindowsTray? _instance;

    /// <inheritdoc />
    public bool IsSupported => true;

    /// <inheritdoc />
    public bool IsHidden { get; private set; }

    /// <inheritdoc />
    public void Attach(Window window, bool startHidden)
    {
        _instance = this;
        _startHidden = startHidden;

        SubscribeToNativeWindow(window);
    }

    /// <summary>
    /// 拿到真正的 Win32 窗口。
    ///
    /// 【为什么要等 Handler 而不是直接用】
    /// MAUI 的 Window 只是一个描述，真正对应的原生窗口要等 Handler 建好才有，
    /// 而它有可能已经是好的（也可能晚一点点）。两种都考虑，谁都不假设。
    /// </summary>
    private void SubscribeToNativeWindow(Window window)
    {
        void Try()
        {
            var native = window.Handler?.PlatformView as Microsoft.UI.Xaml.Window;
            if (native is null)
            {
                return;
            }

            _mainWindowHandle = WindowNative.GetWindowHandle(native);
            _appWindow = AppWindow.GetFromWindowId(
                Microsoft.UI.Win32Interop.GetWindowIdFromWindow(_mainWindowHandle));

            // 拦下"关闭"：这一套软件的价值全在后台那半个，关掉了就等于没装。
            // e.Cancel = true 之后窗口不会被销毁，我们把它藏起来。
            _appWindow.Closing += (_, e) =>
            {
                if (!_closeToTray)
                {
                    return;
                }

                e.Cancel = true;
                HideToTray(notify: !_hasExplainedBalloon);
            };

            CreateTrayIcon();

            if (!_startHidden)
            {
                return;
            }

            // 开机自启的时候别冒出来：这时候人还没打算用这个软件，
            // 只是它的采集和提醒该在后台待着了。
            //
            // 【为什么这里要藏两次】
            // 此刻立刻 Hide() 基本没用——WinUI 拿到句柄之后还会自己 Activate 一次，
            // 把窗口推上来（实测：hide 之后窗口照样可见）。
            // 所以真正的藏必须等它自己站起来之后，也就是第一次 Activated 那一瞬间。
            // 先藏一次是尽量早点消失，最后一次是让它真的消失。
            HideToTray(notify: false);

            void OnActivated(object? sender, Microsoft.UI.Xaml.WindowActivatedEventArgs e)
            {
                native.Activated -= OnActivated;
                HideToTray(notify: false);
            }

            native.Activated += OnActivated;
        }

        if (window.Handler?.PlatformView is Microsoft.UI.Xaml.Window)
        {
            Try();
        }
        else
        {
            window.HandlerChanged += (_, _) => Try();
        }

        // 窗口真的被销毁时把图标摘下来。
        //
        // 【为什么这一行必须单独挂，不能指望别处回收】
        // 系统不会帮忙回收通知区域的图标：进程没了它是没了，
        // 但 explorer 还活得好好的情况下，图标常常要鼠标划过去才消失，
        // 看起来就像"程序关不干净"。
        window.Destroying += Dispose;
    }

    /// <inheritdoc />
    public void HideToTray(bool notify)
    {
        if (_appWindow is null)
        {
            return;
        }

        IsHidden = true;
        _appWindow.Hide();

        if (notify)
        {
            _hasExplainedBalloon = true;
            ShowBalloon("我在这儿", "DayManager 还在后台记录，点这个小图标把它叫回来。");
        }
    }

    /// <inheritdoc />
    public void Restore()
    {
        if (_appWindow is null)
        {
            return;
        }

        try
        {
            _appWindow.Show();

            // 藏过之后直接 Show，有时它会"亮起来但不在最前面"。
            if (_mainWindowHandle != IntPtr.Zero)
            {
                NativeMethods.SetForegroundWindow(_mainWindowHandle);
            }
        }
        catch (Exception ex)
        {
            Services.CrashLogger.Write("恢复窗口", ex);
        }

        IsHidden = false;
    }

    /// <inheritdoc />
    public void SetCloseToTray(bool enabled) => _closeToTray = enabled;

    // ================= 托盘本身 =================

    /// <summary>建立隐形宿主窗口 + 加托盘图标。任何一步失败都要记日志，但不许让程序起不来。</summary>
    private void CreateTrayIcon()
    {
        try
        {
            _taskbarCreatedMessage = NativeMethods.RegisterWindowMessage(TaskbarCreatedName);

            var className = "DayManagerTrayHost";

            var cls = new NativeMethods.WndClassEx
            {
                cbSize = (uint)Marshal.SizeOf<NativeMethods.WndClassEx>(),
                lpfnWndProc = Marshal.GetFunctionPointerForDelegate(Proc),
                lpszClassName = className
            };

            NativeMethods.RegisterClassEx(ref cls);

            _hostWindow = NativeMethods.CreateWindowEx(
                0, className, null, 0, 0, 0, 0, 0,
                // 负数句柄是"只收消息的窗口"：不显示、不进任务栏、不出现在 Alt+Tab 里。
                new IntPtr(-3), IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);

            if (_hostWindow == IntPtr.Zero)
            {
                Services.CrashLogger.Write("建立托盘宿主窗口", new InvalidOperationException(Marshal.GetLastWin32Error().ToString()));
                return;
            }

            AddIcon();
        }
        catch (Exception ex)
        {
            Services.CrashLogger.Write("初始化托盘", ex);
        }
    }

    private void AddIcon()
    {
        if (_hostWindow == IntPtr.Zero)
        {
            return;
        }

        _icon = LoadAppIcon();

        var data = CreateIconData(string.Empty);
        data.uFlags = NifMessage | NifIcon | NifTip;
        data.uCallbackMessage = TrayCallback;

        _iconAdded = NativeMethods.Shell_NotifyIcon(NimAdd, ref data)
                     || NativeMethods.Shell_NotifyIcon(NimModify, ref data);

        if (!_iconAdded)
        {
            Services.CrashLogger.Write("加托盘图标", new InvalidOperationException(Marshal.GetLastWin32Error().ToString()));
        }
    }

    private NativeMethods.NotifyIconData CreateIconData(string info) => new()
    {
        cbSize = (uint)Marshal.SizeOf<NativeMethods.NotifyIconData>(),
        hWnd = _hostWindow,
        uID = 1,
        hIcon = _icon,
        szTip = "DayManager（后台记录中）",
        szInfo = info,
        szInfoTitle = "DayManager"
    };

    /// <summary>
    /// 图标从哪来。
    ///
    /// 【为什么直接从构建产物里读 appicon.ico】
    /// MAUI 会把 AppIcon 生成为 appicon.ico 放在输出目录旁边——已经有了，不必再造一个。
    /// 读不到就退回系统默认图标：一个没图案的托盘图标很难点，
    /// 但比"托盘上什么都没有"要好认得多。
    /// </summary>
    private static IntPtr LoadAppIcon()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "appicon.ico");

        if (File.Exists(path))
        {
            var loaded = NativeMethods.LoadImage(
                IntPtr.Zero, path, 1, 0, 0, NativeMethods.LoadFromFile | NativeMethods.LoadDefaultSize);

            if (loaded != IntPtr.Zero)
            {
                return loaded;
            }
        }

        return NativeMethods.LoadIcon(IntPtr.Zero, 32512); // IDI_APPLICATION
    }

    private void ShowBalloon(string title, string text)
    {
        if (!_iconAdded)
        {
            return;
        }

        var data = CreateIconData(text);
        data.uFlags = NifInfo;
        data.szInfoTitle = title;
        data.uTimeoutOrVersion = 10;

        NativeMethods.Shell_NotifyIcon(NimModify, ref data);
    }

    /// <summary>右键菜单。做成 Win32 原生菜单是因为它长得和操作别的系统图标一样，不需要学习。</summary>
    private void ShowMenu()
    {
        var menu = NativeMethods.CreatePopupMenu();
        NativeMethods.AppendMenu(menu, 0, MenuOpen, "打开 DayManager");

        // 设置也从这里进：程序平时是藏在托盘里的，
        // 那才是"突然想改一下"最常见的时刻——先打开窗口再找入口太绕。
        NativeMethods.AppendMenu(menu, 0, MenuSettings, "设置…");
        NativeMethods.AppendMenu(menu, 0x00000800, 0, string.Empty); // MF_SEPARATOR
        NativeMethods.AppendMenu(menu, 0, MenuQuit, "退出");

        NativeMethods.GetCursorPos(out var point);

        // 不先把窗口推到前台，菜单会在第一次点击时就消失（Windows 的经典要求）。
        NativeMethods.SetForegroundWindow(_hostWindow);

        NativeMethods.TrackPopupMenuEx(
            menu, 0x0002 | 0x0020, point.X, point.Y, _hostWindow, IntPtr.Zero);

        // 弹完要把"前台"还回桌面，否则系统的下一个点击会先去前台我们这个隐形窗口。
        // 表现是"菜单关了之后，第一次点别的地方没反应"——很不起眼，但会让人觉得卡。
        NativeMethods.PostMessage(_hostWindow, 0x0000, IntPtr.Zero, IntPtr.Zero);

        NativeMethods.DestroyMenu(menu);
    }

    /// <summary>托盘回调。跑在 UI 线程上，但恢复窗口是界面操作，还是显式排回去更稳妥。</summary>
    private static IntPtr TrayWndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        var self = _instance;
        if (self is null)
        {
            return NativeMethods.DefWindowProc(hWnd, msg, wParam, lParam);
        }

        if (msg == self._taskbarCreatedMessage)
        {
            // explorer 崩过一次之后会重发这个消息，图标要重新挂上去。
            self.AddIcon();
            return IntPtr.Zero;
        }

        if (msg == TrayCallback)
        {
            var what = (int)(lParam.ToInt64() & 0xFFFF);

            switch (what)
            {
                case WmLButtonUp:
                case NinBalloonUserClick:
                    self.Restore();
                    break;

                case WmRButtonUp:
                    self.ShowMenu();
                    break;
            }

            return IntPtr.Zero;
        }

        if (msg == WmCommand)
        {
            switch ((int)(wParam.ToInt64() & 0xFFFF))
            {
                case MenuOpen:
                    self.Restore();
                    break;

                case MenuSettings:
                    self.Restore();
                    self.OpenSettings();
                    break;

                case MenuQuit:
                    self.Quit();
                    break;
            }

            return IntPtr.Zero;
        }

        return NativeMethods.DefWindowProc(hWnd, msg, wParam, lParam);
    }

    /// <summary>
    /// 跳到设置页。
    ///
    /// 【为什么要排在 Restore 之后】
    /// 导航是界面操作，窗口还藏着的话改了也看不见——
    /// 先把它叫回来，再压进设置页，人才知道发生了什么。
    /// </summary>
    private void OpenSettings()
    {
        Microsoft.Maui.Controls.Application.Current?.Dispatcher.Dispatch(() =>
        {
            _ = Microsoft.Maui.Controls.Shell.Current.GoToAsync("SettingsPage");
        });
    }

    private void Quit()
    {
        RemoveIcon();

        Microsoft.Maui.Controls.Application.Current?.Dispatcher.Dispatch(() =>
        {
            // 走 MAUI 的退出路径，而不是 Environment.Exit：
            // 那样可以触发窗口的 Destroying，把采集的最后一段干净地收尾。
            Microsoft.Maui.Controls.Application.Current?.Quit();
        });
    }

    private void RemoveIcon()
    {
        if (!_iconAdded)
        {
            return;
        }

        var data = CreateIconData(string.Empty);
        NativeMethods.Shell_NotifyIcon(NimDelete, ref data);
        _iconAdded = false;
    }

    public void Dispose(object? sender, EventArgs e) => Dispose();

    public void Dispose()
    {
        // 退出时要连调两次的可能：窗口销毁、出厂Cleanup 各一次。
        // 重复 RemoveIcon 是无害的，但重复 DestroyWindow 会带着一个无效句柄去调系统，
        // 所以用一把锁。
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        RemoveIcon();

        if (_icon != IntPtr.Zero)
        {
            NativeMethods.DestroyIcon(_icon);
            _icon = IntPtr.Zero;
        }

        if (_hostWindow != IntPtr.Zero)
        {
            NativeMethods.DestroyWindow(_hostWindow);
            _hostWindow = IntPtr.Zero;
        }

        if (_instance == this)
        {
            _instance = null;
        }
    }
}
/// <summary>
/// 托盘要用到的那几个 Win32 调用。
///
/// 【为什么这些常数不单独写成常量区块】
/// 它们只在这里出现一次，起个名字反而多一层跳转；
/// 而真正容易记错的（比如 HWND_MESSAGE 那个负数句柄）在使用处有注释。
/// </summary>
internal static class NativeMethods
{
    internal const uint LoadFromFile = 0x0010;
    internal const uint LoadDefaultSize = 0x0040;

    /// <summary>窗口过程。调用约定必须显式声明，否则 .NET 会以默认约定去调 Win32。</summary>
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    internal delegate IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    /// <summary>
    /// NOTIFYICONDATA（V3 尺寸版）。
    ///
    /// 【为什么不用 V4】
    /// V4 那两个额外字段（GUID、自定义气泡图标）这里用不上，
    /// 而 Shell 是靠 cbSize 判断版本的——多出来的长度它不认识，
    /// 少了它会按它以为的长度去读这块内存，两种情况都是图标加不上去。
    /// Marshal.SizeOf 自动算出的长度就是当前位数下的正确值。
    /// </summary>
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct NotifyIconData
    {
        public uint cbSize;
        public IntPtr hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public uint dwState;
        public uint dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public uint uTimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public uint dwInfoFlags;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct WndClassEx
    {
        public uint cbSize;
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        public string? lpszMenuName;
        public string? lpszClassName;
        public IntPtr hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Point
    {
        public int X;
        public int Y;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern bool Shell_NotifyIcon(int dwMessage, ref NotifyIconData pnid);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    internal static extern IntPtr CreateWindowEx(int dwExStyle, string lpClassName, string? lpWindowName, int dwStyle,
        int x, int y, int nWidth, int nHeight, IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    internal static extern ushort RegisterClassEx(ref WndClassEx lpwcx);

    [DllImport("user32.dll")]
    internal static extern IntPtr DefWindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool DestroyWindow(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    internal static extern IntPtr LoadImage(IntPtr hInst, string name, int type, int cx, int cy, uint fuLoad);

    [DllImport("user32.dll")]
    internal static extern IntPtr LoadIcon(IntPtr hInstance, int iconId);

    [DllImport("user32.dll")]
    internal static extern bool DestroyIcon(IntPtr hIcon);

    [DllImport("user32.dll")]
    internal static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    internal static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    internal static extern bool AppendMenu(IntPtr hMenu, uint uFlags, int itemId, string newItem);

    [DllImport("user32.dll")]
    internal static extern bool TrackPopupMenuEx(IntPtr hMenu, uint uFlags, int x, int y, IntPtr hwnd, IntPtr lptpm);

    [DllImport("user32.dll")]
    internal static extern bool DestroyMenu(IntPtr hMenu);

    [DllImport("user32.dll")]
    internal static extern bool GetCursorPos(out Point cursor);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    internal static extern uint RegisterWindowMessage(string name);

    [DllImport("user32.dll")]
    internal static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
}
