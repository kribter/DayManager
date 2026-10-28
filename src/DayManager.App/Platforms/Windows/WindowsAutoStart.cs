using Microsoft.Win32;

namespace DayManager.App.Platforms.Windows;

/// <summary>
/// 开机自启：往注册表 Run 项里写一行。
///
/// 【为什么用注册表而不是"启动"文件夹】
/// 两条路都能做到，但启动文件夹需要一个额外的快捷方式文件——
/// 那个文件会被人删、会在清理软件里被列为"启动项"删一半留一半，
/// 删了之后程序这边还以为自己开着自启。注册表这一行是程序自己的状态，
/// 读和写在同一个地方，不会出现"以为开着其实没开"。
///
/// 【为什么写 HKCU 而不是 HKLM】
/// HKCU 不需要管理员权限。一个对自己弹通知的程序还要 UAC 提权，
/// 第一次安装就把人吓退了。
///
/// 【为什么要带 --background 参数】
/// 开机那一下人还没准备好面对今日清单。自启的目的是让采集和提醒先到位，
/// 界面等他想看的时候从托盘点出来。
/// </summary>
public sealed class WindowsAutoStart : Services.IAutoStartService
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "DayManager";

    /// <summary>告诉程序"这次是开机自己起来的，别露面"。</summary>
    public const string BackgroundArg = "--background";

    private readonly string? _exePath = Environment.ProcessPath;

    /// <inheritdoc />
    public bool IsSupported =>
        _exePath is not null
        && File.Exists(_exePath)
        && !string.Equals(Path.GetFileNameWithoutExtension(_exePath), "dotnet", StringComparison.OrdinalIgnoreCase);

    /// <inheritdoc />
    public bool IsEnabled
    {
        get
        {
            if (!IsSupported)
            {
                return false;
            }

            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
                return key?.GetValue(ValueName) is not null;
            }
            catch (Exception ex)
            {
                Services.CrashLogger.Write("读取开机自启状态", ex);
                return false;
            }
        }
    }

    /// <inheritdoc />
    public bool TrySetEnabled(bool enabled, out string error)
    {
        error = string.Empty;

        if (!IsSupported)
        {
            // 从源码调试启动时 Environment.ProcessPath 是 dotnet.exe，
            // 把它写进 Run 项是错的（下次开机起来的是一个新的 dotnet，没有参数也进不了这个程序）。
            // 宁可直接说清楚，也不要默默写一条无效记录。
            error = "当前不是从程序本体启动的（看不到 exe），不能登记自启。用生成出来的 exe 打开就可以。";

            return false;
        }

        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true)
                            ?? throw new IOException($"打不开注册表项 {RunKeyPath}");

            if (enabled)
            {
                key.SetValue(ValueName, $"\"{_exePath}\" {BackgroundArg}");
            }
            else
            {
                // 不存在也不要紧：删一个没有的值不会报错，而 IsEnabled 早已经是 false 了。
                key.DeleteValue(ValueName, throwOnMissingValue: false);
            }

            return true;
        }
        catch (Exception ex)
        {
            Services.CrashLogger.Write("设置开机自启", ex);
            error = ex.Message;

            return false;
        }
    }

    /// <summary>这次启动是不是"开机自启来的"。</summary>
    public static bool IsLaunchedInBackground() =>
        Environment.GetCommandLineArgs().Any(a =>
            string.Equals(a, BackgroundArg, StringComparison.OrdinalIgnoreCase));
}
