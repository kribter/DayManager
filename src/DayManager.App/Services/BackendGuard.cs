namespace DayManager.App.Services;

/// <summary>
/// 保证"后端是可用的"，不保证它是怎么变可用的。
///
/// 【为什么要有这个类】
/// 对话页和拆解页都需要同一件事：说话之前，先确认后端在。
/// 不在的话先试着自己把它拉起来（它是这个软件的一部分，用户不该需要知道它存在），
/// 拉起来之后还要等它真正能响应——<c>dotnet run</c> 到能接受请求要好几秒，
/// 这期间发请求必然失败。
/// 这段逻辑二十来行，复制两份的后果是：以后改一处忘一处，
/// 表现为"对话页能自动拉起后端，拆解页不行"这种莫名其妙的差异。
/// 所以抽出来，两边共用。
/// </summary>
public sealed class BackendGuard
{
    private readonly ApiClient _api;
    private readonly BackendLauncher _launcher;

    public BackendGuard(ApiClient api, BackendLauncher launcher)
    {
        _api = api;
        _launcher = launcher;
    }

    /// <summary>后端地址，界面上显示"连不上"时要告诉用户连的是哪个地址。</summary>
    public string DisplayAddress => _launcher.DisplayAddress;

    /// <summary>
    /// 确保后端可用，返回结果。
    /// </summary>
    /// <param name="onStatus">
    /// 过程中的状态回调："后端没在跑，正在启动……"这类话要说给用户听，
    /// 否则他看到的是点了按钮之后界面毫无反应，只能猜是不是卡住了。
    /// </param>
    public async Task<HealthResult> EnsureAsync(
        Action<string>? onStatus = null,
        CancellationToken cancellationToken = default)
    {
        // 先用 socket 瞬时探一下端口，别上来就发 HTTP：
        // 端口没人监听时 HTTP 请求要等满 3 秒超时才失败，白白卡三秒。
        // socket 探测 400 毫秒内就有结果。
        if (_launcher.CanAutoStart
            && !await _launcher.IsListeningAsync(TimeSpan.FromMilliseconds(400)))
        {
            onStatus?.Invoke("后端没在跑，正在启动……");

            if (!_launcher.TryStart(out var startError))
            {
                return HealthResult.Fail($"没能自动启动后端：{startError}");
            }

            var (started, health) = await WaitAsync(onStatus, cancellationToken);

            return started
                ? health
                : HealthResult.Fail(
                    $"后端启动了但一直没响应（{_launcher.DisplayAddress}）。"
                    + "直接用 start.bat 启动可以看到它的报错。");
        }

        return await _api.GetHealthAsync(cancellationToken);
    }

    /// <summary>
    /// 等后端真正能响应为止。
    /// </summary>
    /// <returns>
    /// Started=true 表示等到了，Health 是那时的检查结果；
    /// Started=false 表示一直没起来，Health 是最后一次失败的结果。
    ///
    /// 【为什么用元组而不是返回 null 表示失败】
    /// HealthResult 是类，没有 .Value 可用，取值得写 health!.Ok 这种空值容忍写法。
    /// 把"成不成"和"结果"分开成两个值，读起来更清楚，也不依赖可空引用类型。
    /// </returns>
    private async Task<(bool Started, HealthResult Health)> WaitAsync(
        Action<string>? onStatus,
        CancellationToken cancellationToken)
    {
        // 最多等 20 轮。正常情况两三秒就好，
        // 给 20 轮是为了第一次启动时（要加载运行时）不至于刚好卡在边界上。
        HealthResult health = HealthResult.Fail("还没发过请求");

        for (var i = 0; i < 20; i++)
        {
            await Task.Delay(1000, cancellationToken);

            if (i > 0)
            {
                onStatus?.Invoke($"后端正在启动……（已等 {i} 秒）");
            }

            health = await _api.GetHealthAsync(cancellationToken);
            if (health.Ok)
            {
                return (true, health);
            }
        }

        return (false, health);
    }
}
