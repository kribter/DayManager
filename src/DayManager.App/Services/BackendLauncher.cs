using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace DayManager.App.Services;

/// <summary>
/// 客户端自己把后端拉起来。
///
/// 【为什么要这个东西】
/// 后端是 AI 能力的唯一入口（API key 只存在服务端），
/// 但它是一个独立进程。之前只能靠 start.bat 一并拉起，
/// 于是只要绕过脚本直接双击 exe，界面就写着「连不上服务器」——
/// 而用户会以为是程序坏了。这个失败模式完全是人造的：
/// **一个需要"记得先开另一个东西"才能用的功能，本身就是设计缺陷。**
///
/// 【为什么放在客户端里，而不是做成开机自启】
/// 开机自启意味着这台机器每时每刻都跑着一个用不到的服务。
/// 放在客户端里则是"需要时才启动"：不打开拆解页就不启动，
/// 启动了也一直留着（进程是脱离客户端的），下次进来直接复用。
///
/// 【为什么不依赖 MAUI】
/// 跟 SeedFileReader 一样：保持零 MAUI 依赖，测试项目就能用
/// <c>&lt;Compile Include&gt;</c> 编译真身，而不是给它做个替身然后什么都测不到。
/// </summary>
public sealed class BackendLauncher
{
    private readonly IPAddress _address;
    private readonly int _port;
    private readonly string _projectDirectory;
    private readonly string _projectPath;

    private BackendLauncher(
        IPAddress address,
        int port,
        string projectDirectory,
        bool canAutoStart)
    {
        _address = address;
        _port = port;
        _projectDirectory = projectDirectory;
        _projectPath = Path.Combine(projectDirectory, "DayManager.Api.csproj");
        CanAutoStart = canAutoStart;
    }

    /// <summary>
    /// 是否允许自动启动。以下任一条件不满足时都是一个空壳（所有操作直接返回 false）。
    ///
    /// 【最关键的一条：只在本机回环地址上才允许启动】
    /// 以后换成 Tailscale 地址（100.x.x.x）时，后端跑在**另一台机器**上——
    /// 那时在自己电脑上试图启动它，不但没用，还会留下一个反复重启失败的僵尸进程。
    /// 远端地址的后端必须由那台机器自己保证在线，客户端不该碰它。
    /// 这一条判断就是为了防止这种"以后换个地址就悄悄失效"的坑。
    /// </summary>
    public bool CanAutoStart { get; }

    public static BackendLauncher Create(string baseUrl, string repoRoot)
    {
        // 解析失败时用默认值兜底，不让一个配置笔误把自动启动整个废掉。
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri))
        {
            uri = new Uri(SeedFileReader.DefaultBaseUrl);
        }

        var host = uri.Host;
        var port = uri.Port > 0 ? uri.Port : 5080;

        var address = TryParseAddress(host);

        var projectDirectory = Path.Combine(repoRoot, "src", "DayManager.Api");

        // 下面两个条件不满足时，实例仍然存在但 CanAutoStart = false，
        // 这样调用方不用判空，也不会有"注册了个 null 进 DI"这种别扭写法。
        var canAutoStart =
            OperatingSystem.IsWindows()
            && address is not null
            && IPAddress.IsLoopback(address)
            && Directory.Exists(projectDirectory);

        return new BackendLauncher(
            address ?? IPAddress.Loopback, port, projectDirectory, canAutoStart);
    }

    /// <summary>
    /// 后端在不在。用裸 socket 探，不用 HTTP。
    ///
    /// 【为什么这里不能用 HttpClient】
    /// 系统代理会劫持发往 127.0.0.1 的请求并重投给别的进程，
    /// 于是"端口上明明什么都没有"却可能拿到 200。
    /// 判断"端口有没有人监听"这件事必须绕过 HTTP 那一整套，
    /// 直接连 TCP——socket 不读代理环境变量，测到的就是真实的。
    /// </summary>
    public async Task<bool> IsListeningAsync(TimeSpan timeout)
    {
        try
        {
            using var tcp = new TcpClient();
            var connect = tcp.ConnectAsync(_address, _port);

            // WhenAny 是刻意的：TcpClient 的 ConnectAsync 不接受 CancellationToken，
            // 连不上时它要等操作系统超时（几十秒），界面会一直卡着。
            // 用 WhenAny 给一个自己的上限，到点就当"没在跑"。
            if (await Task.WhenAny(connect, Task.Delay(timeout)) != connect)
            {
                //
                // 【为什么要专门接住这个已经不等了的任务】
                // 走到这里说明超时了，可那个 connect 还在后台跑。
                // 它最后一定会抛：要么连接被拒，要么是下面 using 把 socket 释放掉、
                // 把这次 I/O 中止掉（SocketException 995）。
                // 抛出来却没有人 await，就变成"未观察异常"，
                // 最后被记进崩溃日志——看上去像程序出了故障，其实只是"这会儿没人监听"。
                //
                // 这条路径以前只有打开对话页才走一次，现在每次启动都会走
                // （启动即预热），不接住的话日志里会稳定多出两条噪音，
                // 把将来真正要看的故障淹没掉。
                //
                _ = connect.ContinueWith(
                    t => _ = t.Exception,
                    CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted,
                    TaskScheduler.Default);

                return false;
            }

            // await 一次把连接过程中的异常接住（连接被拒会抛 SocketException）。
            await connect;
            return tcp.Connected;
        }
        catch (SocketException)
        {
            return false;
        }
    }

    /// <summary>
    /// 启动后端进程。只负责"点火"，不等待它起来——
    /// 等待是调用方的事，因为调用方才知道要等到什么程度、等不到该怎么告诉用户。
    /// </summary>
    public bool TryStart(out string? error)
    {
        if (!CanAutoStart)
        {
            error = "自动启动不可用（不是 Windows，或后端地址不是本机地址）。";
            return false;
        }

        var dotnet = FindDotnet();
        var assembly = ResolveBuiltAssembly();

        //
        // 【为什么用 UseShellExecute = true 而不是平常那种 false】
        // 这是为了让后端进程**彻底脱离客户端进程**。
        // 用 false 时子进程会继承（或共用）父进程的控制台，
        // 客户端一关，控制台没了，后端也跟着没——
        // 实测确实是这样：进程发起了、也能连上，但父进程退出后它就不见了。
        // 后果是每次开客户端都要重新启动后端，白等几秒；
        // 用 ShellExecute 而非默认的 CreateProcess，是为了让后端在进程关系上尽量独立。
        // 代价是 Arguments 必须自己拼成一个字符串并处理引号，见下面 Quote 的说明。
        // 注意：在这个测试环境里两种写法观察不出差别（环境本身会清理它启动的进程），
        // 选它是出于"桌面程序启动独立进程"的常规做法，不是因为实测出它更好。
        //
        var psi = new ProcessStartInfo
        {
            FileName = dotnet,
            Arguments = assembly is not null
                ? Quote(assembly)
                // 找不到编译产物时才退回 dotnet run（见 ResolveBuiltAssembly 的说明）。
                : $"run --project {Quote(_projectPath)} --no-build --no-launch-profile",

            //
            // 【为什么要显式设 WorkingDirectory】
            // ASP.NET Core 默认把"当前工作目录"当作内容根目录，
            // appsettings.Local.json（存着 AI 密钥）就是从那儿读的。
            // 不设的话子进程继承的是客户端 exe 所在的 bin\... 目录，
            // 那里没有这个配置文件，结果是"后端起得来，但 AI 显示没配好"。
            // 这个坑之前在 start.bat 里已经踩过一次，脚本里用的是 --project。
            //
            WorkingDirectory = _projectDirectory,

            UseShellExecute = true,

            // 后端是个控制台程序，不隐藏会闪一个黑窗口出来。
            WindowStyle = ProcessWindowStyle.Hidden
        };

        try
        {
            // 不重定向输出：子进程的输出不去读的话会填满管道把它卡死，
            // 而我们并不需要看它的输出（要排查时直接用 start.bat 看得到）。
            Process.Start(psi);
            error = null;
            return true;
        }
        catch (Exception ex)
        {
            error = $"{ex.GetType().Name}: {ex.Message}";
            return false;
        }
    }

    /// <summary>端口变了以后重新探测用的地址文本。</summary>
    public string DisplayAddress => $"{_address}:{_port}";

    /// <summary>
    /// 给路径加引号。
    ///
    /// 【为什么必须自己加】
    /// 走 ShellExecute 就没法用 ArgumentList（那个只在 UseShellExecute=false 时生效），
    /// 只能拼成一个字符串。Program Files 这种带空格的路径不加引号会被从空格处截断。
    /// </summary>
    private static string Quote(string value) =>
        value.Contains(' ') ? $"\"{value}\"" : value;

    /// <summary>
    /// 找到编译好的后端程序集，直接用它启动，而不是走 <c>dotnet run</c>。
    ///
    /// 【为什么不直接用 dotnet run，要绕这一下】
    /// <c>dotnet run</c> 本身是一个包装进程，它再派生出真正的服务进程，
    /// 而且启动时要先把整个项目拿去给 MSBuild 评估一遍（慢一两秒）。
    /// 直接跑 dll 的话，起来的就是服务进程本身：少一层进程，启动也快。
    ///
    /// 【一条被推翻过的猜测，别再信】
    /// 曾经以为"后端跟着客户端一起没了"是 <c>dotnet run</c> 这层包装导致的，
    /// 换成直接跑 dll 之后发现照样没了。
    /// 真实原因是**测试环境会清理自己这条命令派生出来的所有进程**，
    /// 与怎么启动无关。真实的客户端是 GUI 程序，没有控制台可被回收，
    /// 关掉客户端时后端是否留存，需要在真机上确认，这里测不出来。
    ///
    /// 【为什么不写死 bin\Debug\net10.0】
    /// 目标框架会随着 .NET 大版本升级而变（net10.0 → net11.0），
    /// 写死路径的话，哪天升级完它就开始报"找不到文件"，而报错还发生在启动后端这种边角地方。
    /// 直接在 bin\Debug 下面按名字找，版本号变了照样能找到。
    /// </summary>
    private string? ResolveBuiltAssembly()
    {
        var binDebug = Path.Combine(_projectDirectory, "bin", "Debug");

        if (!Directory.Exists(binDebug))
        {
            return null;
        }

        // 有多个就取最近改过的那个，避免拿到过期产物。
        return Directory
            .GetFiles(binDebug, "DayManager.Api.dll", SearchOption.AllDirectories)
            .OrderByDescending(path => File.GetLastWriteTimeUtc(path))
            .FirstOrDefault();
    }

    private static IPAddress? TryParseAddress(string host)
    {
        if (IPAddress.TryParse(host, out var address))
        {
            return address;
        }

        // "localhost" 不是 IP 字面量，GetHostAddresses 会解析它。
        // 放在这里而不是每次都解析，是为了让 CanAutoStart 的判断是否成立
        // 不依赖 DNS 是否可用。
        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase))
        {
            return IPAddress.Loopback;
        }

        return null;
    }

    private static string FindDotnet()
    {
        // 客户端启动时 PATH 里不一定有 dotnet（比如从资源管理器直接双击 exe），
        // 所以先按默认安装位置找，找不到再退回交给 PATH 去解析。
        var programFiles = Environment.GetEnvironmentVariable("ProgramFiles");
        if (!string.IsNullOrEmpty(programFiles))
        {
            var candidate = Path.Combine(programFiles, "dotnet", "dotnet.exe");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return "dotnet";
    }
}
