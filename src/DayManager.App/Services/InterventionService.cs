using System.Collections.Concurrent;
using CommunityToolkit.Mvvm.ComponentModel;
using DayManager.Core.Enums;
using DayManager.Core.Intervention;
using DayManager.Core.Planning;
using DayManager.Data.Repositories;

namespace DayManager.App.Services;

/// <summary>
/// 干预引擎：每隔一分钟看一眼"现在在用什么、今天还剩什么没做"，该说话时说话。
///
/// 【为什么这一层这么薄，真正的判断都在 Core 的 InterventionDecider 里】
/// 这里只有两件事是它负责的：**记状态**（上次什么时候提过、是否正在检查）
/// 和**把话送出去**（叫通知器）。判断"该不该提"本身是纯逻辑，
/// 放在 Core 里能被冒烟测试逐条验证——那是这个功能唯一值得认真测的部分。
///
/// 【为什么要定时轮询，而不是"切到娱乐应用时触发一次"】
/// Windows 没有可靠的"切换前台应用"事件（SetWinEventHook 在锁屏、UAC、全屏切换时会漏）。
/// 采集器本身就是 5 秒轮询，再挂一套事件监听只会多出一份会漏的状态。
/// 一分钟轮一次，最坏的代价是提醒晚到 60 秒——而这个功能本来拦的是"已经刷了半小时"，
/// 晚半分钟完全不影响它要解决的问题。
/// </summary>
public partial class InterventionService : ObservableObject, IDisposable
{
    /// <summary>检查间隔。一分钟一次：够及时，也不会把库和 CPU 当跑步机。</summary>
    private static readonly TimeSpan CheckInterval = TimeSpan.FromSeconds(60);

    private readonly ICurrentUsageSource _usage;
    private readonly ItemRepository _items;
    private readonly PolicyRepository _policy;
    private readonly AppRuleRepository _rules;
    private readonly IInterventionNotifier _notifier;

    /// <summary>
    /// 每个应用上一次提醒的时刻。
    ///
    /// 【为什么放内存而不落库】
    /// 冷却只在那一天有意义。重启一次客户端就重新开始冷却，最多让某个应用
    /// 多被提一次——这个代价远小于每次提醒都写一次库。
    /// 而"今天别再提了"是要跨重启生效的（用户按过就必须算数），那一条走库。
    /// </summary>
    private readonly ConcurrentDictionary<string, DateTimeOffset> _lastNotified =
        new(StringComparer.OrdinalIgnoreCase);

    private Timer? _timer;

    /// <summary>0 = 空闲，1 = 正在检查。用 Interlocked 当锁，防止两次检查叠在一起。</summary>
    private int _checking;

    private bool _disposed;

    public InterventionService(
        ICurrentUsageSource usage,
        ItemRepository items,
        PolicyRepository policy,
        AppRuleRepository rules,
        IInterventionNotifier notifier)
    {
        _usage = usage;
        _items = items;
        _policy = policy;
        _rules = rules;
        _notifier = notifier;

        // 点通知上的按钮会从这里回来。切到 UI 线程处理，
        // 因为要改的状态最后会显示在时间页上。
        _notifier.Invoked += OnInvoked;
    }

    /// <summary>
    /// 现在到底在干嘛。这句话会显示在时间页上。
    ///
    /// 【为什么要把内部判断的过程说出来】
    /// 一个只在"该提醒"时才出声的功能，平时完全静默。
    /// 用户没法区分三种情况：它在正常工作但还没到阈值 / 它坏了 / 它压根没启动。
    /// 把判断结果写成一句话，等于给它一个"心跳"——这比任何日志都管用，
    /// 因为用户不需要去翻日志。
    /// </summary>
    [ObservableProperty]
    public partial string StatusText { get; set; } = "还没开始检查";

    /// <summary>通知通道是否可用。不可用时时间页要显示原因，而不是让人以为功能没做。</summary>
    [ObservableProperty]
    public partial bool IsAvailable { get; set; }

    /// <summary>最近一次检查的时刻（本地时间）。</summary>
    [ObservableProperty]
    public partial string LastCheckText { get; set; } = string.Empty;

    /// <summary>启动。注册通知 + 开始轮询。重复调用是安全的。</summary>
    public void Start()
    {
        if (_disposed || _timer is not null)
        {
            return;
        }

        _notifier.Initialize();
        IsAvailable = _notifier.IsAvailable;

        if (!IsAvailable)
        {
            StatusText = "系统通知用不了，提醒发不出去（在 Windows 设置里检查通知权限）";
        }

        _timer = new Timer(_ => _ = CheckAsync(), null, TimeSpan.FromSeconds(3), CheckInterval);
    }

    /// <summary>
    /// 检查一次。失败只记录，不抛——定时器回调里的异常会直接把进程带走。
    /// </summary>
    public async Task CheckAsync()
    {
        if (_disposed || Interlocked.Exchange(ref _checking, 1) == 1)
        {
            return;
        }

        try
        {
            await CheckCoreAsync();
        }
        catch (Exception ex)
        {
            CrashLogger.Write("干预检查", ex);
            StatusText = "检查出错：" + ex.Message;
        }
        finally
        {
            Interlocked.Exchange(ref _checking, 0);
        }
    }

    private async Task CheckCoreAsync()
    {
        var now = DateTimeOffset.Now;
        var today = DateOnly.FromDateTime(now.LocalDateTime);

        LastCheckText = $"最近检查 {now:HH:mm}";

        var enabled = await _policy.GetBoolAsync(PolicyKeys.InterventionEnabled, true);

        if (!enabled)
        {
            StatusText = "干预已关掉";
            return;
        }

        var current = _usage.Current;

        if (current is null)
        {
            // 空闲、锁屏、刚启动还没采到第一个样本，都会走到这里。
            // 这不算异常，但要说出来——否则"一直没提醒"看起来像坏了。
            StatusText = "现在没有在用的应用（空闲或刚启动）";
            return;
        }

        var rule = await _rules.GetAsync(current.AppId);
        var kind = rule?.Kind ?? AppKind.Neutral;

        // 先做最便宜的一步：如果连娱乐都算不上，后面的查库都不必做。
        if (kind == AppKind.Work ||
            (kind == AppKind.Neutral && !EntertainmentApps.IsEntertainment(current.AppId)))
        {
            StatusText = $"正在用 {current.AppName}，它不算娱乐";
            return;
        }

        var context = await BuildContextAsync(current, kind, today, now);
        var decision = InterventionDecider.Decide(context);

        StatusText = Describe(context, decision);

        if (decision.Notice is null)
        {
            return;
        }

        if (!_notifier.Show(decision.Notice.AppId, decision.Notice.Title, decision.Notice.Body))
        {
            StatusText = "想提醒但通知发不出去";
            return;
        }

        _lastNotified[current.AppId] = now;
        StatusText = $"已经提醒过：{decision.Notice.Title}";
    }

    private async Task<InterventionContext> BuildContextAsync(
        CurrentUsageSnapshot current,
        AppKind kind,
        DateOnly today,
        DateTimeOffset now)
    {
        var items = await _items.GetDayListAsync(today);

        // 只有"还没做"的算数。跳过的不计入（那是明确决定今天不做），
        // 放弃的也不计入（已经不在这件事的范畴里）——口径和完成率一致。
        var remaining = items
            .Where(x => x.Status is ItemStatus.Pending or ItemStatus.InProgress)
            .ToList();

        return new InterventionContext(
            current.AppId,
            current.AppName,
            current.MinutesSince(now),
            kind,
            Enabled: true,
            ThresholdMinutes: await _policy.GetIntAsync(PolicyKeys.ThresholdMinutes, 30),
            CooldownMinutes: await _policy.GetIntAsync(PolicyKeys.CooldownMinutes, 30),
            LastNotifiedAt: _lastNotified.TryGetValue(current.AppId, out var last) ? last : null,
            SnoozedOn: await _policy.GetSnoozeDateAsync(current.AppId),
            Today: today,
            RemainingCount: remaining.Count,
            FirstRemainingTitle: remaining.FirstOrDefault()?.Title,
            Now: now);
    }

    /// <summary>把判定结果写成人话，显示在时间页上。</summary>
    private static string Describe(InterventionContext c, InterventionDecision decision)
    {
        var spent = DurationText.Format(c.ContinuousMinutes);

        return decision.Reason switch
        {
            InterventionSkipReason.BelowThreshold =>
                $"{c.AppName} 已连续 {spent}，没到 {c.ThresholdMinutes} 分钟，先不打扰",

            InterventionSkipReason.Snoozed =>
                $"{c.AppName}：今天已经说过「别再提」，今天不再提",

            InterventionSkipReason.CoolingDown =>
                $"{c.AppName} 刚提醒过，{c.CooldownMinutes} 分钟内不重复",

            InterventionSkipReason.NotEntertainment =>
                $"正在用 {c.AppName}，它不算娱乐",

            InterventionSkipReason.Disabled => "干预已关掉",

            _ => decision.Notice is null
                ? $"正在用 {c.AppName}，已连续 {spent}"
                : $"已经提醒过：{decision.Notice.Title}"
        };
    }

    /// <summary>
    /// 用户在通知上点了按钮。「今天别再提了」要落库——重启之后这条承诺仍然有效。
    /// </summary>
    private void OnInvoked(string appId, NoticeAction action)
    {
        if (action != NoticeAction.Snooze)
        {
            return;
        }

        // 通知回调在系统线程上，不能在这里做 IO 或者改界面状态。
        _ = Task.Run(async () =>
        {
            try
            {
                await _policy.SnoozeAsync(appId, DateOnly.FromDateTime(DateTime.Now));
                _notifier.Clear(appId);

                MainThread.BeginInvokeOnMainThread(() =>
                    StatusText = $"{appId}：今天不再提了");
            }
            catch (Exception ex)
            {
                CrashLogger.Write("记录赦免", ex);
            }
        });
    }

    /// <summary>
    /// 发一条测试通知。
    ///
    /// 【为什么必须有这个入口】
    /// 系统通知能不能显示，取决于 Windows 的通知总开关、专注助手、以及这个应用
    /// 有没有被静音——这些都发生在程序外面，程序自己什么都看不出来。
    /// 没有这个按钮，"为什么一条提醒都没有"要排查很久：
    /// 是判定没触发，还是系统把通知吞了？点一下立刻能分清。
    /// </summary>
    public bool SendTestNotice()
    {
        if (!IsAvailable)
        {
            _notifier.Initialize();
            IsAvailable = _notifier.IsAvailable;
        }

        return _notifier.Show(
            "test",
            "这是一条测试提醒",
            "能看到它，说明系统通知这条路是通的。点「知道了」收起来。");
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
        _notifier.Invoked -= OnInvoked;
    }
}
