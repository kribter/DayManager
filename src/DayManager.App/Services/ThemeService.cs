using CommunityToolkit.Mvvm.Messaging;
using DayManager.App.Messages;
using DayManager.Core.Enums;
using DayManager.Data.Repositories;

namespace DayManager.App.Services;

/// <summary>
/// 界面深浅的开关。
///
/// 【为什么要自己管，而不是让用户去改 Windows 主题】
/// 让用户为了这个软件去改系统主题，等于要求他为了开一盏灯去拉总闸。
/// 而且系统是浅色、其它软件各自深色的组合非常常见——
/// 他要的是"这个软件是深的"，不是"这台电脑是深的"。
///
/// 【为什么切换之后必须广播一条消息】
/// 有一批颜色是在 ViewModel 里算出来的（气泡底色、状态徽章、标题的灰）。
/// 它们不会自己变：值是在 Refresh 那一刻读的，主题换了它们还停在旧值上，
/// 界面就会一半深一半浅。所以换完主题要通知各页重新算一遍。
/// XAML 里那部分（AppThemeBinding）是框架自动跟的，不用管。
/// </summary>
public class ThemeService
{
    public const string System = "system";
    public const string Light = "light";
    public const string Dark = "dark";

    private readonly PolicyRepository _policy;

    /// <summary>当前选择。只表示用户选了什么，不代表当前实际是深是浅。</summary>
    public string Mode { get; private set; } = System;

    public ThemeService(PolicyRepository policy)
    {
        _policy = policy;
    }

    /// <summary>
    /// CreateWindow 用的同步版本：自己读库、自己应用。
    ///
    /// 【为什么不留在 MauiProgram.CreateMauiApp 里】
    /// 实测：在 CreateMauiApp 阶段设置 UserAppTheme 是无效的——
    /// 那时 Application 还没和平台窗口挂钩，WinUI 创建窗口时会按系统主题
    /// 把 RequestedTheme 重新定一遍，我们设的值被悄悄盖掉。
    /// 不报错、日志干净，界面就是不变。
    /// CreateWindow 里（窗口对象已建、还没显示）设置才会真正生效，
    /// 而且赶在第一帧排版之前，深色用户不会看见一帧白屏。
    /// </summary>
    public void ApplyFromStore()
    {
        try
        {
            // 【为什么套一层 Task.Run】
            // 这个方法在 CreateWindow 里同步等结果，而那时 UI 线程已经挂上了
            // 自己的同步上下文：直接 await 数据库查询，续体会被排回 UI 线程，
            // 而 UI 线程正在等它——经典死锁，窗口卡死在出生状态。
            // 扔到线程池里跑就没有"排回 UI 线程"这回事了。
            var saved = Task.Run(() => _policy.GetRawAsync(PolicyKeys.AppTheme))
                            .GetAwaiter().GetResult();
            ApplyImmediate(saved);
        }
        catch (Exception ex)
        {
            // 读不出来就退回系统主题：这不该挡住整个软件打开。
            CrashLogger.Write("应用界面主题失败", ex);
        }
    }

    /// <summary>
    /// 启动路径上用的同步版本。
    ///
    /// 【为什么这里要同步】
    /// 如果等异步读库回来再设主题，窗口已经按系统主题画出来了——
    /// 选了深色的用户每次启动都会先闪一下白屏。
    /// 启动阶段在主线程上读一次本地 SQLite 是几十毫秒的事，
    /// 换来的是"从第一帧起就是对的"。
    /// </summary>
    public void ApplyImmediate(string? saved)
    {
        Mode = saved switch
        {
            Light => Light,
            Dark => Dark,
            _ => System,
        };

        Apply(Mode);
    }

    /// <summary>
    /// 换成某一档并记下来。
    /// </summary>
    public async Task SetAsync(string mode)
    {
        Mode = mode switch
        {
            Light or Dark => mode,
            _ => System,
        };

        Apply(Mode);

        // 记成 User：这是用户自己选的，将来 AI 校准参数时不能替他改回去。
        await _policy.SetAsync(PolicyKeys.AppTheme, Mode, PolicySource.User);

        // 让所有页面重算在 C# 里算出来的那些颜色
        WeakReferenceMessenger.Default.Send(new ThemeChangedMessage());
    }

    private static void Apply(string mode)
    {
        // UserAppTheme 是应用级的覆盖：设成 Unspecified 才跟系统走。
        var theme = mode switch
        {
            Light => AppTheme.Light,
            Dark => AppTheme.Dark,
            _ => AppTheme.Unspecified,
        };

        // 【为什么直接赋值，不用 MainThread.BeginInvokeOnMainThread 包一层】
        // "改主题要在主线程"是对的，但这里两个调用点本来就在主线程上：
        //   启动时 —— MauiProgram.CreateMauiApp 就跑在 UI 线程上；
        //   切换时 —— 由界面事件触发，也是主线程。
        // 而包一层在启动阶段是反效果：那时 MAUI 的主线程设施还没就绪，
        // 动作被排进一个尚未开始运转的队列，主题就这么静默丢了——
        // 不报错、日志干净、界面就是不变。实测栽过这一跤。
        if (Application.Current is not null)
        {
            Application.Current.UserAppTheme = theme;
        }
    }
}
