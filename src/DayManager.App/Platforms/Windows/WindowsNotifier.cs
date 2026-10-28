using System.Runtime.InteropServices;
using DayManager.App.Services;
using DayManager.Core.Intervention;
using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;

namespace DayManager.App.Platforms.Windows;

/// <summary>
/// 干预提醒的出口：Windows 系统通知。
///
/// 【为什么用系统通知，而不是 App 里弹一个自绘的窗】
/// 需要被拦的那一刻，你正在别的应用里（B 站、Steam 全屏）。
/// App 内的横幅那时候在后台，根本看不见——看不见的干预等于没做。
/// 系统通知由 Windows 负责显示在最上层，全屏也能出来，这是唯一一条
/// 不依赖"用户先切回本程序"的通路。
///
/// 【为什么必须用 reminder 场景】
/// 普通 toast 会自己滑走，三五秒后就只剩通知中心里一条没人看过的记录。
/// 需求里定的是"提示必须点一下才消失"——那不是为了烦人，
/// 而是因为**自动消失等于没提过**：手在刷视频，眼睛扫到一行字，两秒后就没了。
/// reminder（提醒）场景的通知会停在屏幕上直到用户处理，这是系统提供的唯一一种"不点不走"。
///
/// 【为什么是 unpackaged 应用还要多做一步】
/// 本项目没有打 MSIX 包（WindowsPackageType=None，双击 exe 就能跑）。
/// 没有包身份时，系统不知道"这个进程对应哪个应用"，通知就发不出去。
/// 解法是给进程指定一个固定的 AppUserModelID，再调用 Register 把 COM 激活器注册上。
/// 这一步只在启动时做一次，失败了就退化成"没有通知"——不能因为通知坏了程序起不来。
/// </summary>
public sealed class WindowsNotifier : IInterventionNotifier, IDisposable
{
    /// <summary>
    /// 本应用的通知身份。**不能带空格**，最长 128 字符。
    /// 一旦定下来就别改：改了之后 Windows 会把它当成另一个应用，
    /// 用户之前对通知做的设置（静音、关闭）全部失效，而且旧的通知也会留在通知中心里点不开。
    /// </summary>
    private const string AppUserModelId = "DayManager.Desktop";

    /// <summary>通知分组。同一组里用 Tag 区分，方便按应用替换而不是堆一屏。</summary>
    private const string NoticeGroup = "intervention";

    private bool _registered;
    private bool _disposed;

    /// <summary>
    /// 用户点了通知上的按钮。参数是（应用标识, 点了什么）。
    ///
    /// 【为什么用事件而不是直接在这里写库】
    /// 这个类只负责"把话送出去、把点击收回来"，它不该知道 PolicyRepository 长什么样。
    /// 否则将来要在按钮上再加一个动作（比如"带我去日程页"），
    /// 改动会落在一个纯平台文件里，而那里本来看不见任何业务概念。
    /// </summary>
    public event Action<string, NoticeAction>? Invoked;

    /// <summary>通知系统是否可用。不可用时界面上要能说清原因，而不是点了没反应。</summary>
    public bool IsAvailable => _registered;

    /// <summary>
    /// 注册通知通道。启动时调用一次。
    ///
    /// 【为什么整个方法都包在 try 里】
    /// 通知依赖系统组件（COM 注册、通知服务、用户的通知设置）。
    /// 这些都不归程序管，任何一种不可用都只该导致"没有提醒"，
    /// 而不是"打开程序就崩"。这也符合这个软件的分层：
    /// 采集和提醒是配角，日程本身才是主角，配角坏了不能把主角带走。
    /// </summary>
    public void Initialize()
    {
        if (_registered)
        {
            return;
        }

        try
        {
            // 没有包身份的应用必须先告诉系统"我是谁"。
            // 返回值是 HRESULT，非 0 说明系统拒绝了（极少见），此时继续走 Register 也没有意义。
            var hr = SetCurrentProcessExplicitAppUserModelID(AppUserModelId);
            if (hr != 0)
            {
                Services.CrashLogger.Write(
                    "通知初始化",
                    new InvalidOperationException($"SetCurrentProcessExplicitAppUserModelID 返回 0x{hr:X8}"));
                return;
            }

            AppNotificationManager.Default.NotificationInvoked += OnNotificationInvoked;
            AppNotificationManager.Default.Register();

            _registered = true;
        }
        catch (Exception ex)
        {
            // 通知发不出来不是致命错误，但要留下原因：
            // 用户看到的现象是"说好的提醒一条都没有"，排查时第一句话就是"初始化成功了吗"。
            Services.CrashLogger.Write("通知初始化", ex);
        }
    }

    /// <summary>
    /// 发一条提醒。返回是否真的发出去了。
    ///
    /// 【为什么同一个应用要用固定的 Tag】
    /// 同 Tag 的通知会**替换**而不是叠加。刷视频时每 30 分钟一条，
    /// 一晚上下来通知中心里堆十条"你已经看了 X 分钟"，那已经不叫提醒，叫刷屏。
    /// 替换之后，用户点开通知中心只看到最新的一条。
    /// </summary>
    public bool Show(string appId, string title, string body)
    {
        if (!_registered)
        {
            return false;
        }

        try
        {
            var notification = new AppNotificationBuilder()
                .AddText(title)
                .AddText(body)
                // 两个按钮就是需求里说的那两种收场：
                // 「知道了」是这次到此为止；「今天别再提了」是今天都别再提。
                // 后者的存在是这套功能能被长期留着的前提——没有赦免权的提醒，最终会被整个关掉。
                .AddButton(new AppNotificationButton("知道了")
                    .AddArgument("action", "ok")
                    .AddArgument("app", appId))
                .AddButton(new AppNotificationButton("今天别再提了")
                    .AddArgument("action", "snooze")
                    .AddArgument("app", appId))
                // 不点不收：见类注释里关于自动消失的那段。
                .SetScenario(AppNotificationScenario.Reminder)
                // 提醒场景默认可能循环播放提示音。这里是"提醒"不是"报警"，
                // 一条普通提示音就够——循环音会让人第一反应是去找怎么关掉这个软件。
                .SetAudioEvent(AppNotificationSoundEvent.Default)
                .SetTag(appId)
                .SetGroup(NoticeGroup)
                .BuildNotification();

            AppNotificationManager.Default.Show(notification);
            return true;
        }
        catch (Exception ex)
        {
            Services.CrashLogger.Write("发通知", ex);
            return false;
        }
    }

    /// <summary>把某个应用还挂着的通知撤掉（比如用户已经自己把这段用完了）。</summary>
    public void Clear(string appId)
    {
        if (!_registered)
        {
            return;
        }

        try
        {
            // 这是个"顺手清一下"的动作，调用方不需要等它完成；
            // 显式写成 _ = 是为了告诉编译器"这里故意不 await"，而不是漏了。
            _ = AppNotificationManager.Default.RemoveByTagAsync(appId);
        }
        catch
        {
            // 通知已经不在了、或者系统组件短暂不可用，都不影响任何数据。
            // 这类"清屏没成功"不值得留日志，更不值得打断调用方。
        }
    }

    private void OnNotificationInvoked(AppNotificationManager sender, AppNotificationActivatedEventArgs args)
    {
        // 参数已经是拆好的字典（键就是 AddArgument 里写的那两个），不需要自己解析字符串。
        var values = args.Arguments;

        // 没有 app 参数说明不是我们发的通知（比如系统自己的提醒被误接）——
        // 不能猜一个应用去赦免，那会把无关的应用静音一整天。
        if (!values.TryGetValue("app", out var appId) || string.IsNullOrWhiteSpace(appId))
        {
            return;
        }

        var action = values.TryGetValue("action", out var raw) && raw == "snooze"
            ? NoticeAction.Snooze
            : NoticeAction.Dismissed;

        try
        {
            Invoked?.Invoke(appId, action);
        }
        catch (Exception ex)
        {
            Services.CrashLogger.Write("处理通知点击", ex);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (!_registered)
        {
            return;
        }

        try
        {
            AppNotificationManager.Default.NotificationInvoked -= OnNotificationInvoked;
            AppNotificationManager.Default.Unregister();
        }
        catch
        {
            // 退出路径上的失败不值得报错：进程马上要结束了，注册表里的项下次启动会重新写。
        }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int SetCurrentProcessExplicitAppUserModelID(string appUserModelId);
}
