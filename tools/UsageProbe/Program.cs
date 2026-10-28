// 采集探针：在不开界面的情况下验证「前台窗口采集」这一段真的能用。
//
// 用法（在项目根目录）：
//   dotnet run --project tools/UsageProbe -- <数据库路径> <采样秒数>
// 两个参数都可以不传：默认用真实库，采 25 秒。

using System.Runtime.InteropServices;
using DayManager.App.Platforms.Windows;
using DayManager.Data;
using DayManager.Data.Repositories;
using Microsoft.EntityFrameworkCore;

var dbPath = args.Length > 0 && !args[0].StartsWith("--") ? args[0] : @"F:\DayManager\data\daymanager.db";
var seconds = args.Length > 1 && int.TryParse(args[1], out var s) ? s : 25;

// --simulate：每隔几秒假装动一下鼠标。
// 【为什么需要这个开关】采集器判"人不在"用的是系统最后一次输入的时间。
// 人坐着看屏幕不动手，一次采样都过不了空闲这一关——那是设计正确，但也就测不到写入那一段。
// 这个开关只为了让链路能被验证，不代表真实使用。
var simulate = args.Contains("--simulate");

// --audio：只读音频电平，不碰数据库。
// 【为什么单独开一个模式】验证"看视频不动手也被记下来"这条链路时，
// 需要人配合放一段声音才能看到峰值变化；把这一步和采集分开，
// 就能先确认 COM 在这台机器上到底能不能读到电平，不用每次都等一轮采样。
if (args.Contains("--audio"))
{
    var times = args.Length > 1 && int.TryParse(args[1], out var n) ? n : 15;

    Console.WriteLine($"音频电平诊断：读 {times} 次（每 2 秒一次）。");
    Console.WriteLine("想看\"有声音\"的效果，就在这段时间里放点音乐或视频。");
    Console.WriteLine();

    var meter = new SystemAudioMeter { Trace = Console.WriteLine };

    // --playtest：真放一段系统声音，看电平会不会跟着起来。
    // 【为什么必须有这一段】只读静音时的 0.0000 不能证明任何事——
    // "读不了"和"确实没声音"都显示 0。必须看到 0 → 非 0 → 0 的变化，才算验证过。
    var wav = @"C:\Windows\Media\Alarm01.wav";
    var canPlay = args.Contains("--playtest") && File.Exists(wav);

    for (var i = 0; i < times; i++)
    {
        if (canPlay && i == 3)
        {
            Console.WriteLine($"  >>> 开始播放 {wav}（会响几秒）");
            Play(wav, true);
        }

        if (canPlay && i == times - 2)
        {
            Console.WriteLine("  >>> 停止播放");
            Play(null, false);
        }

        await Task.Delay(TimeSpan.FromSeconds(2));

        var playing = meter.IsPlaying();
        Console.WriteLine($"  [{i + 1:00}] 电平 {meter.LastPeak:F4}  → 有声音在放？{(playing ? "是" : "否")}" +
                          (meter.IsDisabled ? "  （信号源已停用）" : ""));

        if (meter.IsDisabled)
        {
            Console.WriteLine("  读不了，后面不用再试了。");
            return 1;
        }
    }

    return 0;

    // winmm 的 PlaySound：SND_FILENAME | SND_ASYNC（| SND_LOOP），传 null 停止。
    // 【为什么不用 SoundPlayer】它在 .NET Core 里属于单独的兼容包，为一个探针加依赖不值。
    static void Play(string? path, bool loop) => Beep.Play(path, loop);
}



if (args.Contains("--audiodiag")) { AudioDiag.Run(); return 0; }

if (!File.Exists(dbPath))
{
    Console.WriteLine($"找不到数据库：{dbPath}");
    return 2;
}

var factory = new ProbeFactory(dbPath);
var repo = new UsageRepository(factory);
var tracker = new WindowsUsageTracker(repo)
{
    // 采不到数据时，这一行行就是全部线索。
    Trace = Console.WriteLine
};

Console.WriteLine($"数据库：{dbPath}");

// 【为什么要先把空闲时间打出来】
// 采集器第一件事就是判空闲：超过 3 分钟没键鼠输入，它一条都不记。
// 探测的时候如果人正坐着看屏幕、手没动过，结果就是"一条都没有"——
// 而这跟"采集坏了"在输出上长得一模一样。先把这个数摆出来，省掉一次误判。
var idleNow = IdleProbe.GetIdleSeconds();
Console.WriteLine($"当前空闲：{idleNow:F0} 秒" + (idleNow > 180 ? "（超过 3 分钟，采集会认为人不在，什么都不会记——动一下鼠标再来）" : ""));

Console.WriteLine($"开始采样 {seconds} 秒。这段时间请正常切几个窗口（别让它一直停在同一个窗口上）。");

using var sim = simulate ? new InputSimulator() : null;
if (sim is not null)
{
    Console.WriteLine("（--simulate 已开启：每 4 秒动一下鼠标，只为了让空闲判断放行）");
}

await tracker.StartAsync();
await Task.Delay(TimeSpan.FromSeconds(seconds));
await tracker.StopAsync();

Console.WriteLine();
Console.WriteLine("刚落库的会话（时间已转本地）：");

await using var db = factory.CreateDbContext();
var rows = await db.UsageSessions
    .OrderByDescending(x => x.EndAt)
    .Take(12)
    .ToListAsync();

if (rows.Count == 0)
{
    Console.WriteLine("  （一条都没有 —— 采集没生效，先看 logs 目录下有没有「使用时长采集启动失败」）");
    return 1;
}

var now = DateTimeOffset.UtcNow;
foreach (var r in rows.OrderBy(x => x.StartAt))
{
    var start = r.StartAt.ToLocalTime();
    var end = r.EndAt.ToLocalTime();
    var wall = (int)(end - start).TotalSeconds;
    Console.WriteLine(
        $"  {start:HH:mm:ss} → {end:HH:mm:ss}  {r.DurationSeconds,5}s  " +
        $"(墙钟 {wall}s)  {r.AppName}  [{r.AppId}]  {(r.IsEntertainment ? "娱乐" : "")}");
    Console.WriteLine($"        标题：{r.WindowTitle ?? "（没有）"}");
}

// 最近这一段如果是刚刚写下的，说明采集链路通了；再检查时长没被墙钟带偏。
var latest = rows.OrderByDescending(x => x.EndAt).First();
var latestWall = (int)(latest.EndAt.ToLocalTime() - latest.StartAt.ToLocalTime()).TotalSeconds;
Console.WriteLine();
Console.WriteLine(latestWall - latest.DurationSeconds <= 5
    ? "结论：时长和起止区间对得上（差值 ≤ 5 秒），累加逻辑正常。"
    : $"注意：时长比区间少 {latestWall - latest.DurationSeconds} 秒——中间有空档，这是预期行为（空档不算使用）。");

return 0;

internal class ProbeFactory(string dbFile) : IDbContextFactory<AppDbContext>
{
    public AppDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite($"Data Source={dbFile}")
            .Options;
        return new AppDbContext(options);
    }
}

/// <summary>只用来读一下"距上次键鼠输入过了多久"，诊断用。</summary>
internal static class IdleProbe
{
    [StructLayout(LayoutKind.Sequential)]
    private struct LastInputInfo
    {
        public uint StructSize;
        public uint LastTick;
    }

    [DllImport("user32.dll")]
    private static extern bool GetLastInputInfo(ref LastInputInfo plii);

    public static double GetIdleSeconds()
    {
        var info = new LastInputInfo { StructSize = (uint)Marshal.SizeOf<LastInputInfo>() };
        if (!GetLastInputInfo(ref info))
        {
            return 0;
        }

        var elapsed = Environment.TickCount - (long)info.LastTick;
        return elapsed < 0 ? 0 : elapsed / 1000.0;
    }
}

/// <summary>
/// 每 4 秒把鼠标挪 1 像素再挪回来。
///
/// 【为什么用这个老掉牙的 mouse_event 而不是 SendInput】
/// 它只需要一行 P/Invoke，不用拼那个 40 字节的 INPUT 结构。
/// 这里的目的只有一个：让系统的"最后一次输入时间"往前走，
/// 好让采集器知道"人在"，从而走到写入那一段。不是产品代码，够用就行。
/// </summary>
internal sealed class InputSimulator : IDisposable
{
    private const uint MouseEventMove = 0x0001;

    private readonly CancellationTokenSource _cts = new();

    public InputSimulator()
    {
        _ = Task.Run(async () =>
        {
            while (!_cts.IsCancellationRequested)
            {
                mouse_event(MouseEventMove, 1, 0, 0, IntPtr.Zero);
                mouse_event(MouseEventMove, -1, 0, 0, IntPtr.Zero);
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(4), _cts.Token);
                }
                catch (TaskCanceledException)
                {
                    return;
                }
            }
        });
    }

    [DllImport("user32.dll")]
    private static extern void mouse_event(uint dwFlags, int dx, int dy, uint dwData, IntPtr dwExtraInfo);

    public void Dispose()
    {
        _cts.Cancel();
        _cts.Dispose();
    }
}

/// <summary>
/// 一次性诊断：把"设备不支持电平表"和"我的 COM 调用姿势不对"分开。
/// 做法是用同一个 Activate 换几个已知 IID 去试：
/// 全失败 → 是调用姿势的问题；只有电平表失败 → 是这台设备的限制。
/// </summary>
internal static class AudioDiag
{
    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int dataFlow, int stateMask, out IntPtr devices);
        [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice device);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, uint clsCtx, IntPtr p, out IntPtr instance);
        [PreserveSig] int OpenPropertyStore(int stgm, out IntPtr store);
        [PreserveSig] int GetId(out IntPtr id);
        [PreserveSig] int GetState(out int state);
    }

    public static void Run()
    {
        Console.WriteLine($"线程 apartment：{Thread.CurrentThread.GetApartmentState()}");

        var t = Type.GetTypeFromCLSID(new Guid("BCDE0395-E52F-467C-8E3D-C4579291692E"));
        var obj = Activator.CreateInstance(t!);
        var en = (IMMDeviceEnumerator)obj!;
        Console.WriteLine("enumerator 创建成功");

        var hr = en.GetDefaultAudioEndpoint(0, 0, out var dev);
        Console.WriteLine($"GetDefaultAudioEndpoint hr=0x{hr:X8}");
        if (hr < 0 || dev is null) return;

        Console.WriteLine($"GetState hr=0x{dev.GetState(out var st):X8}  state={st}");
        Console.WriteLine($"GetId    hr=0x{dev.GetId(out var idp):X8}  id={Marshal.PtrToStringUni(idp)}");

        foreach (var (name, iid) in new (string, string)[]
        {
            ("IAudioMeterInformation", "C02216F6-8C67-4B5B-9D00-D008E73E0064"),
            ("IAudioEndpointVolume",   "5CDF2C82-841E-4546-9722-0CF74078229A"),
            ("IAudioClient",           "1CB9AD4C-DBFA-4c32-B178-C2F568A703B2"),
            ("IPropertyStore",         "886d8eeb-8cf2-4446-8d02-cdba1dbdcf99"),
        })
        {
            var g = new Guid(iid);
            var h = dev.Activate(ref g, 23, IntPtr.Zero, out var p);
            Console.WriteLine($"  Activate {name,-24} hr=0x{h:X8}  ptr={p}");
            if (p != IntPtr.Zero) Marshal.Release(p);
        }
    }
}

/// <summary>用 winmm 的 PlaySound 播一段系统声音，只为让"有没有声音在放"能被真验证一次。</summary>
internal static class Beep
{
    private const uint SND_FILENAME = 0x00020000;
    private const uint SND_ASYNC = 0x0001;
    private const uint SND_LOOP = 0x0008;

    public static void Play(string? path, bool loop) =>
        PlaySound(path, IntPtr.Zero, SND_FILENAME | SND_ASYNC | (loop ? SND_LOOP : 0));

    [DllImport("winmm.dll", CharSet = CharSet.Unicode)]
    private static extern bool PlaySound(string? pszSound, IntPtr hmod, uint fdwSound);
}
