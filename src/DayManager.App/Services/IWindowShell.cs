namespace DayManager.App.Services;

/// <summary>
/// 窗口的"去留"控制：把界面藏起来但让程序继续跑。
///
/// 【为什么需要这一层，而不是在界面代码里直接操作窗口】
/// 这是个纯平台能力——MAUI 没有统一的托盘 API（甚至连"藏到托盘"这个概念都没有），
/// Windows 靠 <c>Shell_NotifyIcon</c>，别的平台各有各的做法或者干脆做不到。
/// 界面层只能对着这个抽象说话，不能知道 Win32 的存在。
///
/// 【为什么参数是能力而不是偏好】
/// 这一层不负责记"用户上一次选了什么"，那是 <c>PolicySetting</c> 的事。
/// 藏着枚举让它变成多端的，会把简单的开关变成一份要跨端同步的状态。
/// </summary>
public interface IWindowShell
{
    /// <summary>这个平台能不能把窗口藏到托盘。不能时界面上要说明，而不是点了没反应。</summary>
    bool IsSupported { get; }

    /// <summary>窗口这会儿是不是被藏着。界面上的按钮文字要跟着变。</summary>
    bool IsHidden { get; }

    /// <summary>
    /// 把主窗口挂到托盘上。<c>startHidden</c> 用于开机自启的场景：起来就别露面。
    /// </summary>
    void Attach(Window window, bool startHidden);

    /// <summary>
    /// 藏到托盘。<c>notify</c> 表示要不要弹一句气泡提示。
    /// 第一次被 X 掉的时候必须弹——不然窗口凭空消失，人只会以为程序崩了。
    /// </summary>
    void HideToTray(bool notify);

    /// <summary>把藏起来的窗口叫回来。</summary>
    void Restore();

    /// <summary>
    /// 点标题栏的 X 时是"藏起来"还是"真退出"。默认藏着（详见 WindowsTray 的说明）。
    /// </summary>
    void SetCloseToTray(bool enabled);
}
