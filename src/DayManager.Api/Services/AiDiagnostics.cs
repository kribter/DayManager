namespace DayManager.Api.Services;

/// <summary>
/// 把 AI 调用的关键诊断追加到文件里。
///
/// 【为什么要落文件，而不是用 ILogger】
/// 后端由客户端拉起（BackendLauncher：隐藏窗口、不重定向输出），
/// ILogger 写的控制台谁也看不到。而"AI 偶发返回空"这类问题，
/// 恰恰只有靠 finish_reason、token 用量这些字段才能分辨原因——
/// 上游不会解释，证据不留下，问题就永远停在"偶发"。
/// 2026-10-27 一次真实教训：用户连续三条消息都得到空回复，
/// 事后想查，控制台早已随进程烟消云散，什么也查不了。
/// </summary>
public static class AiDiagnostics
{
    private static readonly object Gate = new();

    public static void Log(string line)
    {
        try
        {
            var dir = ResolveLogsDir();
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, "api-ai.log");

            lock (Gate)
            {
                File.AppendAllText(path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {line}{Environment.NewLine}");
            }
        }
        catch
        {
            // 诊断是锦上添花。写日志自己出错绝不能拖垮请求链路。
        }
    }

    /// <summary>
    /// 日志放仓库根的 logs/ 下。查找逻辑和 Program.ResolveDbPath 一致——
    /// 故意不抽成公共方法：就两处，抽了反而让"日志在哪"这种事多一层间接。
    /// </summary>
    private static string ResolveLogsDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (dir.GetFiles("DayManager.slnx").Length > 0 || dir.GetFiles("*.sln").Length > 0)
            {
                return Path.Combine(dir.FullName, "logs");
            }

            dir = dir.Parent;
        }

        return Path.Combine(AppContext.BaseDirectory, "logs");
    }
}
