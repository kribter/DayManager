using DayManager.Core.Intervention;

namespace DayManager.App.Services;

/// <summary>
/// 干预提醒的出口。
///
/// 【为什么要有这层接口，而不是直接调用 WindowsNotifier】
/// 提醒这件事每个平台的形态完全不同：Windows 是系统通知，
/// Android 以后是通知渠道（NotificationChannel），而采集器也是各平台一份。
/// 把这些差异挡在接口后面，干预的判定逻辑（什么时候该说话、说什么）
/// 就能一份代码在两端共用——那部分才是真正复杂、真正容易做错的地方。
/// </summary>
public interface IInterventionNotifier
{
    /// <summary>
    /// 注册通知通道。启动时调用一次，重复调用应当是安全的。
    /// 失败不能抛出去——通知坏了只是没有提醒，程序还得照常用。
    /// </summary>
    void Initialize();

    /// <summary>这个平台的通知能不能用。不能用时界面上要能说清，而不是点了没反应。</summary>
    bool IsAvailable { get; }

    /// <summary>用户点了通知上的按钮。参数是（应用标识, 点了什么）。</summary>
    event Action<string, NoticeAction>? Invoked;

    /// <summary>发一条提醒。返回是否真的发出去了。</summary>
    bool Show(string appId, string title, string body);

    /// <summary>把某个应用还挂着的通知撤掉。</summary>
    void Clear(string appId);
}
