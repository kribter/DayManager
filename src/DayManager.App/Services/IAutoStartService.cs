namespace DayManager.App.Services;

/// <summary>
/// 开机自启。
///
/// 【为什么它是单独一个接口，而不是塞进 IWindowShell】
/// 托盘是"这一次的窗口怎么摆"，自启是"下一次开机要不要起来"——
/// 两件事只因为这会儿都只有 Windows 实现才显得像一类。
/// 将来某端能做托盘但不能自启，把它们捆在一起就得传一个假的实现进去。
///
/// 【为什么写注册表失败要返回 false 而不是抛异常】
/// 失败的可能有两种：没有权限，或者应用不是从 exe 启动的（比如开发时 dotnet run）。
/// 两种都不是"出 bug 了"，而是"这件事在这儿做不到"——界面要能解释清楚，
/// 而不是弹一个看不懂的堆栈。
/// </summary>
public interface IAutoStartService
{
    /// <summary>这台机器上能不能设置自启。</summary>
    bool IsSupported { get; }

    /// <summary>注册表里现在是不是已经登记了。</summary>
    bool IsEnabled { get; }

    /// <summary>
    /// 登记或取消登记。
    /// </summary>
    /// <returns>成功与否。失败时 <paramref name="error"/> 是一句能直接给人看的原因。</returns>
    bool TrySetEnabled(bool enabled, out string error);
}
