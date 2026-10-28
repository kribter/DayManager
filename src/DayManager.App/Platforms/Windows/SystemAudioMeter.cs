using System.Runtime.InteropServices;

namespace DayManager.App.Platforms.Windows;

/// <summary>
/// 读系统默认播放设备的输出电平，用来回答一个问题：**电脑上是不是正在放声音**。
///
/// 【为什么需要这个】
/// 判断"人在不在"原本用的是"最后一次键鼠输入距今多久"，超过 3 分钟就认为人走了。
/// 但看视频、看直播、听歌、打游戏的时候，人十几分钟不碰键鼠再正常不过——
/// 于是"刷了两小时 B 站"只被记进开头三分钟。
/// 而娱乐时长恰恰是干预引擎和 AI 排期最重要的输入：数字是假的，
/// AI 会真诚地说"你今天很自律"。这不是精度问题，是方向错了。
///
/// 有声音在放，说明电脑正在为人产出东西——这是一个独立于键鼠的"人在"信号。
///
/// 【为什么不用 SMTC（系统媒体传输控制）】
/// WinRT 的 GlobalSystemMediaTransportControlsSession 语义更好（能拿到"正在播放"状态和标题），
/// 但它是 WinRT API，需要 CsWinRT 投影，采集器被 tools/UsageProbe 以源码方式链接时
/// 会拖进一整套 WinRT 依赖。这里只需要一个布尔值，Core Audio 是纯 COM，
/// 在任何 Windows 进程里都能跑，探针也能直接验证。
///
/// 【已知的边界（接受）】
/// 放着歌去睡觉，会被记成"在用"。但电脑确实在为你干活，而且锁屏那类情况
/// 已经由采集器的 NotRealUsage 名单排掉了。
/// 反过来，静音看视频（比如只看字幕）这里读不到，由采集器的媒体名单兜底。
/// </summary>
internal sealed class SystemAudioMeter
{
    /// <summary>低于这个电平就当没声音。0.5%：躲开静音时的底噪，又不会漏掉很小的音量。</summary>
    private const float PeakThreshold = 0.005f;

    /// <summary>连续失败这么多次之后就彻底不问了。</summary>
    private const int FailureLimit = 5;

    // Core Audio 的固定标识。这两个值取自 Windows SDK 的 endpointvolume.h / mmdeviceapi.h
    // （本机在 C:\Program Files (x86)\Windows Kits\10\Include\10.0.26100.0\um\），不要凭记忆改。
    private static readonly Guid ClsidMMDeviceEnumerator = new("BCDE0395-E52F-467C-8E3D-C4579291692E");

    /// <summary>
    /// 【这个值踩过坑】C02216F6 后面半段很容易被记错成别的接口的 GUID
    /// （我第一版写成 ...-8C51-4A81-A397-2C1F0CB3BA4B，Activate 直接返回 E_NOINTERFACE）。
    /// 而 E_NOINTERFACE 和"这台设备的驱动不支持电平表"长得一模一样，
    /// 不对照头文件就会往错的方向排查很久。
    /// </summary>
    private static readonly Guid IidIAudioMeterInformation = new("C02216F6-8C67-4B5B-9D00-D008E73E0064");

    private const int DataFlowRender = 0;   // eRender：播放设备（不是麦克风）
    private const int RoleConsole = 0;      // eConsole：默认的那个
    private const uint ClsCtxAll = 23;      // CLSCTX_ALL

    private int _failures;
    private bool _disabled;

    /// <summary>排查用的观察点，和采集器同一个套路：静默失败必须留下话。</summary>
    public Action<string>? Trace { get; set; }

    /// <summary>彻底坏了没有。true 表示这个信号源不可用，采集器退回"只看键鼠"。</summary>
    public bool IsDisabled => _disabled;

    /// <summary>
    /// 上一次读到的电平（0~1）。
    ///
    /// 【为什么要留着它】"判成没声音"有两种可能：真的没声音，或者这台机器读不了电平。
    /// 只有看到这个数是 0.000 还是 0.037，才分得清——否则排查只能靠猜。
    /// </summary>
    public float LastPeak { get; private set; }

    /// <summary>
    /// 现在有没有声音在放。
    ///
    /// 【为什么每次都重新创建 COM 对象，不缓存】
    /// COM 对象绑在创建它的那个线程上（apartment）。采集器跑在定时器线程上，
    /// 而定时器每次回调不一定是同一个线程池线程——缓存下来跨线程用，
    /// 会在某些机器上随机失败，而且是那种"重启一下就好了"的失败，最难查。
    /// 每次重建也就几十微秒，而这个方法只在"人不动"的时候才被调用，频率很低。
    /// </summary>
    public bool IsPlaying()
    {
        if (_disabled)
        {
            return false;
        }

        object? enumeratorObj = null;
        IMMDevice? device = null;
        IntPtr meterPtr = IntPtr.Zero;
        object? meterObj = null;

        try
        {
            var type = Type.GetTypeFromCLSID(ClsidMMDeviceEnumerator);
            if (type is null)
            {
                return Fail("拿不到 MMDeviceEnumerator 的类型");
            }

            enumeratorObj = Activator.CreateInstance(type);
            if (enumeratorObj is not IMMDeviceEnumerator enumerator)
            {
                return Fail("创建出来的对象不是 IMMDeviceEnumerator");
            }

            var hr = enumerator.GetDefaultAudioEndpoint(DataFlowRender, RoleConsole, out device);
            if (hr < 0 || device is null)
            {
                return Fail($"取默认播放设备失败（HRESULT 0x{hr:X8}）");
            }

            var iid = IidIAudioMeterInformation;
            hr = device.Activate(ref iid, ClsCtxAll, IntPtr.Zero, out meterPtr);
            if (hr < 0 || meterPtr == IntPtr.Zero)
            {
                // 数字输出（S/PDIF、HDMI 某些模式）不支持电平表，Activate 会失败。
                // 这不是故障，只是这台机器用不了这个信号源——禁用掉，别反复试。
                return Fail($"这个播放设备不支持电平表（HRESULT 0x{hr:X8}），音频信号源停用");
            }

            meterObj = Marshal.GetObjectForIUnknown(meterPtr);
            var meter = (IAudioMeterInformation)meterObj;

            hr = meter.GetPeakValue(out var peak);
            if (hr < 0)
            {
                return Fail($"读电平失败（HRESULT 0x{hr:X8}）");
            }

            _failures = 0;
            LastPeak = peak;
            return peak > PeakThreshold;
        }
        catch (Exception ex)
        {
            return Fail($"{ex.GetType().Name}：{ex.Message}");
        }
        finally
        {
            // COM 对象不走垃圾回收的节奏（RCW 释放不及时），
            // 不手动放会一路涨到句柄耗尽——一个"只是记录员"的功能不该这么占资源。
            if (meterObj is not null)
            {
                Marshal.ReleaseComObject(meterObj);
            }

            if (meterPtr != IntPtr.Zero)
            {
                Marshal.Release(meterPtr);
            }

            if (device is not null)
            {
                Marshal.ReleaseComObject(device);
            }

            if (enumeratorObj is not null)
            {
                Marshal.ReleaseComObject(enumeratorObj);
            }
        }
    }

    private bool Fail(string reason)
    {
        _failures++;

        // 只报第一次：连续失败时每次采样都喊一声，日志会被同一句话淹没。
        if (_failures == 1)
        {
            Trace?.Invoke($"[音频] {reason}");
        }

        if (_failures >= FailureLimit)
        {
            _disabled = true;
            Trace?.Invoke($"[音频] 连续失败 {_failures} 次，不再尝试——改回只看键鼠输入");
        }

        return false;
    }

    [ComImport]
    [Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        /// <summary>
        /// 声明在 GetDefaultAudioEndpoint 前面、但这里用不到。
        /// 【为什么必须声明】COM 接口的方法是按声明顺序排成函数指针表的，
        /// 少声明一个，后面所有方法的位置就全错位了——调用到的是另一个函数。
        /// </summary>
        [PreserveSig]
        int EnumAudioEndpoints(int dataFlow, int stateMask, out IntPtr devices);

        [PreserveSig]
        int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice device);
    }

    [ComImport]
    [Guid("D666063F-1587-4E43-81F1-B948E807363F")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        [PreserveSig]
        int Activate(ref Guid iid, uint clsCtx, IntPtr activationParams, out IntPtr instance);
    }

    /// <summary>
    /// 【这个 Guid 必须和 Windows SDK 里的一字不差】
    /// 转型成接口时，运行时是拿**接口上写的这个 Guid** 去 QueryInterface 的，
    /// 不是拿传给 Activate 的那个（那个只决定 Activate 返回什么）。
    /// 写成错的，现象是 InvalidCastException / E_NOINTERFACE，
    /// 和"这台设备不支持电平表"完全一样——排查时极易走错方向。
    /// </summary>
    [ComImport]
    [Guid("C02216F6-8C67-4B5B-9D00-D008E73E0064")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioMeterInformation
    {
        [PreserveSig]
        int GetPeakValue(out float peak);
    }
}
