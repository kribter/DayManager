using System.Diagnostics;
using System.Text;

namespace DayManager.App.Services;

/// <summary>
/// 把没接住的异常写进文件。
///
/// 【为什么必须有这个东西】
/// 桌面程序崩了就是"窗口没了"，没有控制台、没有弹窗、没有任何线索。
/// 一个人开发的时候，没有崩溃日志等于只能靠猜——
/// 而猜错方向的代价是反复改一些根本无关的代码。
/// 有了堆栈，绝大多数闪退一眼就能定位到行。
///
/// 【为什么挂在三个地方】
/// 异常从不同的地方冒出来，走的路径不一样：
///   AppDomain.UnhandledException    —— 兜底，进程退出前的最后一道
///   TaskScheduler.UnobservedTaskException —— 后台任务里没人 await 的异常（默认不崩，但会静默丢数据）
///   WinUI Application.UnhandledException  —— XAML 解析、绑定、界面事件里的异常最先到这里
/// 少挂一个就可能漏掉某一类，所以三个都接。
///
/// 【为什么不弹窗】
/// 崩溃发生的那一刻 UI 线程可能已经不可用了，弹窗本身会二次失败。
/// 写文件是最稳的：崩了之后打开文件就知道发生了什么。
/// </summary>
public static class CrashLogger
{
    private static readonly object Gate = new();
    private static string? _dir;

    /// <summary>
    /// 日志目录。界面上「打开日志」的入口会用同一个路径。
    ///
    /// 【为什么不能写死仓库路径——2026-11-16 的事故】
    /// 这里曾写死 F:\DayManager\logs，结果发给别人（对方机器没有 F 盘）后，
    /// Init() 里 CreateDirectory 直接抛异常，而它又是 CreateMauiApp 的第一句、
    /// 早于窗口创建——表现为「双击毫无反应、任务管理器里也没有」。
    /// 修复模式照抄 MauiProgram 里数据库路径的做法：仓库根在（= 本机开发）
    /// 就用仓库 logs；不在（= 发给别人）就落到 %LOCALAPPDATA%\DayManager\logs。
    /// </summary>
    public static string LogDirectory
    {
        get
        {
            if (_dir is null)
            {
                if (OperatingSystem.IsWindows())
                {
                    // 仓库根在（= 本机开发）→ 用仓库 logs，习惯不变；
                    // 不在（= 发给别人）→ 和数据库的便携目录同一个家。
                    _dir = Directory.Exists(SeedFileReader.RepoRoot)
                        ? Path.Combine(SeedFileReader.RepoRoot, "logs")
                        : Path.Combine(
                            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                            "DayManager", "logs");
                }
                else
                {
                    _dir = FileSystem.AppDataDirectory;
                }
            }

            return _dir;
        }
    }

    public static void Init()
    {
        // 【兜底者自己绝不能成为崩溃源】
        // 创建目录失败（权限、异常环境）时宁可没有日志，也不能让日志器
        // 在 CreateMauiApp 的第一句话里把整个进程带走——Write() 里本来就是
        // "写失败就吞掉"的态度，初始化对齐同一条。
        try
        {
            Directory.CreateDirectory(LogDirectory);
        }
        catch
        {
        }

        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Write("未处理异常", e.ExceptionObject as Exception, e.IsTerminating);

        TaskScheduler.UnobservedTaskException += (_, e) =>
            Write("后台任务异常", e.Exception, false);
    }

    public static void Write(string kind, Exception? ex, bool isTerminating = false)
    {
        var sb = new StringBuilder();
        sb.AppendLine("================================================================");
        sb.AppendLine($"时间     ：{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}");
        sb.AppendLine($"类型     ：{kind}{(isTerminating ? "（程序即将退出）" : string.Empty)}");

        var depth = 0;
        for (var cur = ex; cur is not null; cur = cur.InnerException, depth++)
        {
            if (depth > 0)
            {
                sb.AppendLine("  ---- 内层异常 ----");
            }

            sb.AppendLine($"异常     ：{cur.GetType().FullName}");
            sb.AppendLine($"消息     ：{cur.Message}");
        }

        sb.AppendLine("堆栈     ：");
        sb.AppendLine(ex?.StackTrace ?? "（没有堆栈）");
        sb.AppendLine();

        try
        {
            lock (Gate)
            {
                File.AppendAllText(
                    Path.Combine(LogDirectory, $"crash-{DateTime.Now:yyyyMMdd}.log"),
                    sb.ToString(),
                    Encoding.UTF8);
            }
        }
        catch
        {
            // 写日志本身失败时什么都不做：没有可以汇报的地方，
            // 再抛一次只会把真正的异常盖掉。
        }
    }
}
