using System.Diagnostics;
using System.Runtime.InteropServices;

namespace DayManager.App.Platforms.Windows;

/// <summary>
/// 保证同一时刻只有一个实例在跑。
///
/// 【为什么这是托盘功能的组成部分，而不是额外的一件事】
/// 藏起来的窗口看不见。人的第一反应是"没打开？"——于是双击第二次。
/// 一旦真的起来两个进程，后果不是"多一个窗口"那么轻：
/// 两个采集器各采各的，同一段时间被记两次，时长翻倍；
/// 两个干预引擎各弹各的，同一件事提醒两遍。
/// 而数据看上集合法（每一条记录都是真的），查错时只会往库里找。
///
/// 【为什么第二个实例要负责把第一个叫出来】
/// 它什么都不做的话，用户双击之后屏幕毫无变化，
/// 结论只能是"这个软件打不开了"——比让他重新打火还糟糕。
/// 双击的意图是"我要用它"，把它送到眼前就是正确的回应。
/// </summary>
public static class SingleInstance
{
    private const string MutexName = "Local\\DayManager.SingleInstance.v1";

    private const string TrayHostClassName = "DayManagerTrayHost";

    private static Mutex? _mutex;

    /// <summary>
    /// 试试自己是不是第一个。
    /// </summary>
    /// <returns>true = 我是唯一的，可以继续；false = 已经有人在跑了。</returns>
    public static bool TryBecomeOwner()
    {
        _mutex = new Mutex(initiallyOwned: true, MutexName, out var createdNew);

        // 所有权要靠 _mutex 活到进程结束。被回收的话，
        // 互斥量会被释放，第二个实例就能进来——问题会从"稳定复现"变成"偶尔出现"。
        GC.KeepAlive(_mutex);

        return createdNew;
    }

    /// <summary>把已经跑着的那个窗口叫出来。</summary>
    public static void FocusExisting()
    {
        var self = Environment.ProcessId;

        var others = Process.GetProcessesByName("DayManager.App")
            .Where(p => p.Id != self)
            .Select(p => p.Id)
            .ToHashSet();

        if (others.Count == 0)
        {
            return;
        }

        IntPtr target = IntPtr.Zero;

        NativeMethods.EnumWindows((hWnd, _) =>
        {
            NativeMethods.GetWindowThreadProcessId(hWnd, out var pid);

            if (!others.Contains((int)pid))
            {
                return true; // 继续找
            }

            var className = GetClassName(hWnd);

            // 托盘那个宿主窗口是隐藏的，把它搬出来没有任何意义。
            if (className == TrayHostClassName)
            {
                return true;
            }

            target = hWnd;
            return false; // 找到了，停下
        }, IntPtr.Zero);

        if (target == IntPtr.Zero)
        {
            return;
        }

        NativeMethods.ShowWindow(target, 9); // SW_RESTORE
        NativeMethods.SetForegroundWindow(target);
    }

    private static string GetClassName(IntPtr hWnd)
    {
        var buffer = new System.Text.StringBuilder(256);
        NativeMethods.GetClassName(hWnd, buffer, buffer.Capacity);

        return buffer.ToString();
    }

    private static class NativeMethods
    {
        internal delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll")]
        internal static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

        [DllImport("user32.dll")]
        internal static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

        [DllImport("user32.dll")]
        internal static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll")]
        internal static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        internal static extern int GetClassName(IntPtr hWnd, System.Text.StringBuilder buffer, int maxCount);
    }
}
