using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using DayManager.Core.Enums;
using DayManager.Core.Intervention;
using DayManager.Data.Repositories;

namespace DayManager.App.Platforms.Windows;

/// <summary>
/// Windows 端的前台窗口采集。
///
/// 【为什么放在 Platforms/Windows 下】
/// 这套东西全部依赖 user32.dll，在手机上连编译都过不去。
/// 放在平台目录里，别的平台根本看不到它，也就不需要写一堆 #if 把它包起来。
/// Android 那边将来要换成 UsageStatsManager，是另一份实现，各写各的更清楚。
///
/// 【为什么是采样而不是监听事件】
/// Windows 确实有"前台窗口变化"的事件（SetWinEventHook），
/// 但它在锁屏、UAC 弹窗、全屏游戏切换时会漏事件，而且回调在非 UI 线程上触发，
/// 写库要自己做线程同步。定时采样（默认 5 秒）实现简单、行为可预测，
/// 误差最多 5 秒——判断"今天在 B 站待了多久"完全够用。
/// 精确不是这里的目标，稳定不出错才是：这份数据是干预引擎的唯一输入。
/// </summary>
public sealed class WindowsUsageTracker : ICurrentUsageSource, IDisposable
{
    /// <summary>采样间隔。5 秒：精度够用，一整天也就一万七千次，开销可忽略。</summary>
    private static readonly TimeSpan SampleInterval = TimeSpan.FromSeconds(5);

    /// <summary>
    /// 超过这么久没有键鼠输入，就认为人已经离开，这段时间不计入使用。
    ///
    /// 【为什么必须有这个判断】
    /// 不判断的话，挂着浏览器去睡觉的八小时会被记成"使用时长"，
    /// 于是"今天用了 10 小时"这种数字一出台就是假的。
    /// 而它是干预引擎和 AI 排期的唯一输入——输入错了，后面每一句劝告都建立在谎话上。
    /// </summary>
    private static readonly TimeSpan IdleThreshold = TimeSpan.FromMinutes(3);

    /// <summary>
    /// 娱乐名单**只有一份**，在 <see cref="DayManager.Core.Intervention.EntertainmentApps"/>。
    ///
    /// 【为什么这里原来有一份硬编码的，后来删掉了】
    /// M7 做采集时，名单是为了给"娱乐时长"这个统计数字分类；
    /// M9 做干预时又需要判断"该不该拦"，如果各留一份，
    /// 迟早会出现"时间页说这个应用算娱乐，但干预拦不住"——
    /// 两个地方各说各话，而且都不会报错，只能靠人对着两个页面比才发现。
    ///
    /// 合并时口径也收紧了一点：音乐播放器、知乎这类不再算娱乐。
    /// 它们在工作时是背景音和查资料，把它们算进"娱乐 3 小时"，
    /// 那个数字就不再是"我今天逃避了多久"，而只是一个没用的大数。
    /// </summary>

    /// <summary>
    /// 这些进程有窗口，但**不算在用电脑**——它们是"人不在"的标志。
    ///
    /// 【为什么必须排掉锁屏】
    /// 锁屏之后前台窗口是 LockApp，标题还写着"Windows 默认锁屏界面"。
    /// 不排掉的话，走开锁屏的头三分钟（还没到空闲阈值）会被记成"在用锁屏界面"，
    /// 甚至成为当天使用时长里的一行，而这是纯粹的噪声。
    ///   LockApp    —— 锁屏界面本身
    ///   LogonUI    —— 登录界面（有些机器上锁屏走这个）
    /// </summary>
    private static readonly HashSet<string> NotRealUsage = new(StringComparer.OrdinalIgnoreCase)
    {
        "lockapp", "logonui", "lockapp.exe", "logonui.exe"
    };

    /// <summary>
    /// 这些应用**不需要人一直动手**也在持续产出内容：播放器、音乐、游戏。
    /// 前台是它们时，即使长时间没有键鼠输入，也算在用。
    ///
    /// 【为什么这份名单要和娱乐名单分开】
    /// 娱乐名单里还有知乎、微博这类——刷它们是要一路滚动的，
    /// 三分钟不碰键鼠基本可以断定人已经走了（可能只是没关掉）。
    /// 把整个娱乐名单都豁免，等于"娱乐应用开着就算在用"，那和挂机睡觉就没区别了。
    /// 这里只豁免"放着就会自己往下走"的那一部分。
    ///
    /// 浏览器看视频不在这个名单里（同一个浏览器也用来干活），
    /// 那种情况靠音频信号兜住——这也是为什么音频是主要判据、这份名单只是兜底。
    /// </summary>
    private static readonly HashSet<string> PassiveMediaApps = new(StringComparer.OrdinalIgnoreCase)
    {
        // 播放器
        "potplayer", "potplayermini", "vlc", "mpv", "mpvnet", "wmplayer", "video.ui",
        "smplayer", "kmplayer", "mplayer",
        // 音乐
        "qqmusic", "neteasemusic", "cloudmusic", "kugou", "kuwo", "foobar2000",
        "musicbee", "spotify", "itunes", "aimp", "listen1",
        // 游戏（手柄操作、或者正在看剧情/等待匹配）
        "steam", "wegame", "genshin", "yuanren", "honkai", "lol", "tencentgames",
        "epicgameslauncher", "origin", "uplay",
        // 客户端视频站（它们自己会播下一集）
        "bilibili", "douyu", "huya", "youku", "iqiyi", "tencentvideo"
    };

    private readonly UsageRepository _usage;

    /// <summary>
    /// 音频信号：看视频、听歌时人是不碰键鼠的，光靠输入判断会把这段时间全丢掉。
    /// 它只在"已经空闲超时"时才被问一次，频率很低，开销可以忽略。
    /// </summary>
    private readonly SystemAudioMeter _audio = new();

    private Timer? _timer;
    private Guid _currentSessionId;
    private string _currentAppId = string.Empty;

    /// <summary>
    /// 现在这一段的快照（谁、从几点开始）。给干预引擎读。
    ///
    /// 【为什么不是让干预引擎自己去查库】
    /// 库里那一段的 StartAt 是"这一段开始"没错，但要拿到它就得先知道 sessionId；
    /// 而"现在到底是哪一段"只有采集器自己清楚（切应用、从空闲回来都会换段）。
    /// 让查询方去猜，等于把采集器的内部状态复制一份到外面——
    /// 两边的判断只要有一处不一致，就会出现"提醒里说你在 B 站，其实你早换到编辑器了"。
    ///
    /// 【为什么是 volatile 字段而不是属性加锁】
    /// 写入方是采样线程，读取方是干预线程，读写都只是替换一个引用。
    /// 引用赋值本身是原子的，volatile 保证"读的时候一定能看到最新那次写入"，
    /// 对这个场景已经够了；加锁只会让每 5 秒的采样多一次争抢。
    /// </summary>
    private volatile CurrentUsageSnapshot? _current;

    /// <summary>当前这一段在用什么。没人用（空闲、锁屏、刚起来）时为 null。</summary>
    public CurrentUsageSnapshot? Current => _current;

    /// <summary>
    /// 上一次**计入时间**的采样时刻。为 null 表示"刚从空闲里回来"或"还没开始"，
    /// 下一次采样只重新对表、不加时间——否则那段空档会被算进使用时长。
    /// </summary>
    private DateTimeOffset? _lastCreditedAt;

    /// <summary>上一次"人到底在不在"的结论。只在结论**变了**的时候才打日志。</summary>
    private string? _lastVerdict;

    private bool _disposed;

    /// <summary>
    /// 已经停过了。
    ///
    /// 【为什么单独一个标记，而不是复用 _disposed】
    /// StopAsync 之后可能还有一次采样正在飞（定时器回调已经进了线程池）。
    /// 它会在停止之后又开出一段新会话，而那时已经没人收尾了——
    /// 库里就留下一条永远没有结束时间的记录。所以停止之后一律不再采样。
    /// </summary>
    private volatile bool _stopped;

    public WindowsUsageTracker(UsageRepository usage)
    {
        _usage = usage;

        // 音频那边的话也走同一条日志通道。它失败时是静默的
        // （界面上表现就是"时间页少了一块"），必须能看见原因。
        _audio.Trace = Say;
    }

    /// <summary>
    /// 排查用的观察点：采集**为什么没数据**时，这是唯一能说清原因的东西。
    ///
    /// 【为什么采集器需要这个，而别的服务不需要】
    /// 这里的失败全是静默的——人离开了、取不到前台窗口、写库抛异常，
    /// 三种情况在界面上长得一模一样：时间页一片空白。
    /// 没有这一行日志，排查就只能靠猜。
    /// </summary>
    public Action<string>? Trace { get; set; }

    private void Say(string message) => Trace?.Invoke($"[采集] {message}");

    /// <summary>
    /// 开始采集。返回"收拾了几条上次没正常结束的记录"，只用于日志。
    /// </summary>
    public async Task<int> StartAsync(CancellationToken ct = default)
    {
        var orphans = await _usage.CloseOrphansAsync(DateTimeOffset.Now, ct);
        Say($"启动，收拾了 {orphans} 条上次没正常结束的记录");

        // 采样不在 UI 线程上做：P/Invoke 和写库都不该挡着界面。
        _timer = new Timer(_ => _ = SampleAsync(), null, TimeSpan.Zero, SampleInterval);
        return orphans;
    }

    /// <summary>停止采集（退出程序时调用），把当前这段收尾。</summary>
    public async Task StopAsync()
    {
        _stopped = true;
        _timer?.Dispose();
        _timer = null;

        if (_currentSessionId == Guid.Empty)
        {
            return;
        }

        var id = _currentSessionId;
        _currentSessionId = Guid.Empty;
        _currentAppId = string.Empty;
        _lastCreditedAt = null;
        _current = null;

        // 只是把结束时间停在最后一次采样。时长早就累加好了，
        // 这时候再按时间差重算一遍，反而会把关机的空档算进去。
        await _usage.ExtendAsync(id, DateTimeOffset.Now);
    }

    /// <summary>一次采样：看看现在前台是谁，空闲就什么都不做。</summary>
    private async Task SampleAsync()
    {
        if (_disposed || _stopped)
        {
            return;
        }

        try
        {
            var info = GetForegroundApp();
            if (info is null)
            {
                // 取不到（锁屏、没有前台窗口）：结束当前这段，等下次再开。
                await CloseCurrentAsync(DateTimeOffset.Now);
                NoteVerdict(null, null);
                return;
            }

            var now = DateTimeOffset.Now;

            // 人不在就不记。并且**在这里收口**，让回来之后自成一段。
            // 【为什么不在原来的那条上"接着用"】
            // 时长是按每次采样累加的，空档本来进不去；但一条会话的起止时间还留在库里，
            // 而"几点最有效率"那张柱状图是按起止区间往小时桶里摊的，
            // 中间去吃饭的那一小时就会被摊到柱子上——
            // 表现为总时长 45 分钟、柱子加起来 1.5 小时，两个数打架。
            // 在离开的地方切断，区间就干净了；按应用汇总不受影响（它按 AppId 合并）。
            var idle = GetIdleTime();
            if (idle > IdleThreshold)
            {
                // 没碰键鼠不等于人走了：看视频、听歌、打游戏都是一动不动的。
                // 而娱乐时长恰恰是干预引擎最看重的输入——漏掉它，
                // AI 会看着"今天很自律"的数据真诚地夸你。
                var presence = WhyStillHere(info.Value);
                if (presence is null)
                {
                    NoteVerdict("away", _currentSessionId != Guid.Empty
                        ? $"空闲 {(int)idle.TotalSeconds} 秒，人应该走了，这一段的时长停在 {_lastCreditedAt:HH:mm:ss}"
                        : null);

                    await CloseCurrentAsync(_lastCreditedAt ?? now);
                    return;
                }

                NoteVerdict(presence.Value.Key, presence.Value.Message);
            }
            else
            {
                NoteVerdict(null, null);
            }

            // 换应用了，或者刚才没有会话：旧的收尾，新的开始。
            if (_currentSessionId == Guid.Empty ||
                !string.Equals(_currentAppId, info.Value.AppId, StringComparison.OrdinalIgnoreCase))
            {
                await CloseCurrentAsync(now);
                await OpenAsync(info.Value, now);
                return;
            }

            // 同一个应用：把这一次间隔加进去。
            if (_lastCreditedAt is null)
            {
                // 刚对完表（比如刚从空闲里回来但会话还在）。空档不计，从现在重新算。
                _lastCreditedAt = now;
                return;
            }

            var delta = now - _lastCreditedAt.Value;
            if (delta <= TimeSpan.Zero)
            {
                return;
            }

            // 【为什么要夹一下】
            // 电脑睡眠时定时器不走，醒来后第一次采样会看到"过了 8 小时"。
            // 真按 8 小时加，一天的"使用时长"就被睡眠时间污染了，
            // 而这个数是后面所有判断的输入。
            // 超过三个采样间隔就认为中间断过：这一段到上次采样为止，从现在另起一段。
            if (delta > SampleInterval * 3)
            {
                await CloseCurrentAsync(_lastCreditedAt.Value);
                await OpenAsync(info.Value, now);
                return;
            }

            _lastCreditedAt = now;
            await _usage.AddActiveSecondsAsync(_currentSessionId, (int)delta.TotalSeconds, now, info.Value.Title);
        }
        catch (Exception ex)
        {
            // 采集绝不能因为一次失败就把程序拖垮：
            // 它只是个记录员，用户的日程才是主角。失败这一次，五秒后再来。
            // 但原因要留下——不然"时间页一直是空的"只能靠猜。
            Say($"采样失败：{ex.GetType().Name} {ex.Message}");
        }
    }

    /// <summary>
    /// 已经空闲超时了，但人可能还在——看视频、听歌、打游戏都是一动不动的。
    /// 返回 null 表示确实走了。
    ///
    /// 【为什么先看名单再看声音】
    /// 名单是纯粹的字符串比较，几乎不要钱；读音频要创建 COM 对象，贵得多。
    /// 音频是主要判据（浏览器看视频全靠它），但它只在名单没命中时才问。
    /// </summary>
    private Presence? WhyStillHere(ForegroundInfo info)
    {
        if (PassiveMediaApps.Contains(info.AppId))
        {
            return new Presence("media", $"前台是 {info.AppName}（这类应用自己会往下走），没碰键鼠也算在用");
        }

        if (_audio.IsPlaying())
        {
            return new Presence("audio", "有声音在放，没碰键鼠也算在用");
        }

        return null;
    }

    /// <summary>
    /// 记一次"人在不在"的结论，但**相同的结论只说一次**。
    /// 每 5 秒喊一嗓子会把日志淹掉，真正有用的是结论**变化**的那一刻。
    /// </summary>
    private void NoteVerdict(string? key, string? message)
    {
        if (key == _lastVerdict)
        {
            return;
        }

        _lastVerdict = key;

        if (message is not null)
        {
            Say(message);
        }
    }

    /// <summary>开一段新会话。切换应用、刚从空闲里回来、睡眠断档之后都走这里。</summary>
    private async Task OpenAsync(ForegroundInfo info, DateTimeOffset at)
    {
        var session = await _usage.StartAsync(
            // 必须写全名：MAUI 自己也有一个 DevicePlatform（Android/iOS/WinUI 那套），
            // 在这里会撞名，编译直接报"引用不明确"。
            DayManager.Core.Enums.DevicePlatform.Windows,
            info.AppId,
            info.AppName,
            info.Title,
            DayManager.Core.Intervention.EntertainmentApps.IsEntertainment(info.AppId),
            at);

        _currentSessionId = session.Id;
        _currentAppId = info.AppId;

        // 这一段从"现在"开始算，本次采样不加时间——
        // 加了就等于把切换的这 5 秒同时记给新旧两个应用。
        _lastCreditedAt = at;

        _current = new CurrentUsageSnapshot(info.AppId, info.AppName, at, info.Title);

        Say($"开始记录 {info.AppName}（{info.AppId}）{(string.IsNullOrWhiteSpace(info.Title) ? "" : "：" + info.Title)}");
    }

    /// <summary>把当前这段收口。传进来的时刻是"最后一次确认人在用"的时刻，不是关窗那一刻。</summary>
    private async Task CloseCurrentAsync(DateTimeOffset at)
    {
        _lastCreditedAt = null;

        if (_currentSessionId == Guid.Empty)
        {
            return;
        }

        var id = _currentSessionId;
        _currentSessionId = Guid.Empty;
        _currentAppId = string.Empty;

        // 先清快照再收尾：清晚了的话，收尾那几毫秒里干预引擎会读到
        // "还在用上一个应用"，并可能据此弹一条已经过时的提醒。
        _current = null;

        await _usage.ExtendAsync(id, at);
    }

    private static ForegroundInfo? GetForegroundApp()
    {
        var handle = NativeMethods.GetForegroundWindow();
        if (handle == IntPtr.Zero)
        {
            return null;
        }

        var title = new StringBuilder(512);
        NativeMethods.GetWindowText(handle, title, title.Capacity);

        _ = NativeMethods.GetWindowThreadProcessId(handle, out var pid);
        if (pid == 0)
        {
            return null;
        }

        string appId;
        string appName;
        try
        {
            var proc = Process.GetProcessById((int)pid);
            appId = proc.ProcessName;

            // 进程名是 msedge、chrome 这种，给用户看太别扭。
            // 文件描述里是"Microsoft Edge"这样的正式名字，有就用它。
            appName = ReadableName(proc) ?? proc.ProcessName;
        }
        catch (ArgumentException)
        {
            // 进程已经退出（采样和关闭撞上了）。这次就不记了。
            return null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }

        // 有些系统进程的窗口不属于任何有意义的应用（比如桌面本身、锁屏界面），
        // 记下来只会污染统计——"今天用了 3 小时 explorer"没有参考价值。
        if (string.IsNullOrWhiteSpace(appId) || NotRealUsage.Contains(appId))
        {
            return null;
        }

        return new ForegroundInfo(appId, appName, title.Length > 0 ? title.ToString() : null);
    }

    /// <summary>
    /// 取一个给人看的应用名（"Microsoft Edge" 而不是 "msedge"）。
    /// 取不到就返回 null，由调用方退回进程名——显示成 msedge 也比显示空白强。
    /// </summary>
    private static string? ReadableName(Process proc)
    {
        try
        {
            var desc = proc.MainModule?.FileVersionInfo.FileDescription;
            return string.IsNullOrWhiteSpace(desc) ? null : desc.Trim();
        }
        catch
        {
            // 系统进程、提权进程连 MainModule 都读不到（权限不足）。
            // 这不是故障，只是拿不到更好看的名字，不值得为它中断采样。
            return null;
        }
    }

    private static TimeSpan GetIdleTime()
    {
        var info = new NativeMethods.LastInputInfo();
        info.StructSize = (uint)Marshal.SizeOf(info);

        if (!NativeMethods.GetLastInputInfo(ref info))
        {
            // 拿不到就当成"一直在用"。宁可多记，也不要把正常使用记成没用。
            return TimeSpan.Zero;
        }

        var elapsed = Environment.TickCount - (long)info.LastTick;
        return elapsed < 0 ? TimeSpan.Zero : TimeSpan.FromMilliseconds(elapsed);
    }

    private readonly record struct ForegroundInfo(string AppId, string AppName, string? Title);

    /// <summary>一次"人还在"的判定：Key 用来判断结论有没有变，Message 是给人看的原因。</summary>
    private readonly record struct Presence(string Key, string Message);

    private static class NativeMethods
    {

        [DllImport("user32.dll")]
        internal static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        internal static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int count);

        [DllImport("user32.dll")]
        internal static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

        [DllImport("user32.dll")]
        internal static extern bool GetLastInputInfo(ref LastInputInfo plii);

        [StructLayout(LayoutKind.Sequential)]
        internal struct LastInputInfo
        {
            public uint StructSize;
            public uint LastTick;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _timer?.Dispose();
        _timer = null;
    }
}
