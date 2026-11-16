using System.Globalization;
using CommunityToolkit.Mvvm.Messaging;
using DayManager.App.Messages;
using DayManager.Core.Entities;
using DayManager.Core.Enums;
using DayManager.Core.Intervention;
using DayManager.Core.Proactive;
using DayManager.Data.Repositories;

namespace DayManager.App.Services;

/// <summary>
/// AI 主动开口：定时看一眼"该不该说话"，该说就让后端生成一句，然后弹通知 + 写进对话。
///
/// 【为什么触发判断放在客户端，而不是后端起个定时任务】
/// 两条硬理由：
///   1. **通知能力只在客户端**。后端是个控制台进程，弹不出 toast；
///      就算它能弹，客户端关着的时候弹给谁看。
///   2. **后端不知道客户端还活着没有**。客户端退出了后端还在跑（实测过的），
///      那时它自说自话，没人收得到。
/// 所以客户端负责"什么时候说"，后端只负责"说什么"。
///
/// 【为什么要写进对话记录，光弹通知不行吗】
/// 通知会滑走。滑走之后这句话就彻底消失了——
/// 用户没法回它、没法撤它，甚至记不清它说过什么。
/// 写进对话，它就成了这段关系里真实发生过的一句：
/// 打开对话页看得见，能接着往下聊。
/// </summary>
public sealed class ProactiveNudgeService : IDisposable
{
    /// <summary>检查间隔。和干预引擎同一个节奏：一分钟一次。</summary>
    private static readonly TimeSpan CheckInterval = TimeSpan.FromSeconds(60);

    /// <summary>启动后先等一会儿再检查：刚开机那阵子后端还在预热，别抢。</summary>
    private static readonly TimeSpan FirstDelay = TimeSpan.FromSeconds(25);

    /// <summary>
    /// 通知用这个前缀打 Tag。
    ///
    /// 【为什么要能被认出来】用户点「今天别再提了」时，回调带回的是这个字符串，
    /// 得靠它区分"这是 AI 说的话"和"这是某个娱乐应用被拦了"。
    /// </summary>
    public const string NoticePrefix = "ai.";

    private readonly ItemRepository _items;
    private readonly ChatRepository _chat;
    private readonly PolicyRepository _policy;
    private readonly ApiClient _api;
    private readonly BackendGuard _guard;
    private readonly AiSettings _ai;
    private readonly IInterventionNotifier? _notifier;

    private Timer? _timer;
    private int _running;
    private bool _disposed;

    /// <summary>最近一次开口的结果。显示在时间页上（这个功能平时是静默的，得有个心跳）。</summary>
    public string StatusText { get; private set; } = "还没检查过";

    /// <summary>状态变了（时间页要跟着更新）。</summary>
    public event Action? StatusChanged;

    public ProactiveNudgeService(
        ItemRepository items,
        ChatRepository chat,
        PolicyRepository policy,
        ApiClient api,
        BackendGuard guard,
        AiSettings ai,
        IInterventionNotifier? notifier = null)
    {
        _items = items;
        _chat = chat;
        _policy = policy;
        _api = api;
        _guard = guard;
        _ai = ai;
        _notifier = notifier;

        if (_notifier is not null)
        {
            _notifier.Invoked += OnNotifierInvoked;
        }
    }

    /// <summary>开始定时检查。重复调用是安全的。</summary>
    public void Start()
    {
        if (_disposed || _timer is not null)
        {
            return;
        }

        _timer = new Timer(_ => _ = CheckAsync(), null, FirstDelay, CheckInterval);
    }

    /// <summary>
    /// 检查一次。
    /// </summary>
    public async Task CheckAsync()
    {
        // 同一时刻只跑一个检查：AI 一轮要几十秒，一分钟的间隔很容易叠上去。
        if (Interlocked.CompareExchange(ref _running, 1, 0) != 0)
        {
            return;
        }

        try
        {
            await CheckCoreAsync();
        }
        catch (Exception ex)
        {
            // 【为什么这里必须接住】
            // Timer 回调里抛出的异常会一路冒到线程池，最后要么被记成"未观察异常"
            // 污染崩溃日志，要么直接让进程静默死掉。
            // 而"检查一次失败"本身完全不需要用户知道——下一分钟还会再试。
            SetStatus("检查时出错了（不影响使用，下一分钟会再试）");
            CrashLogger.Write("AI 主动开口检查", ex);
        }
        finally
        {
            Interlocked.Exchange(ref _running, 0);
        }
    }

    private async Task CheckCoreAsync()
    {
        var now = DateTimeOffset.Now;
        var today = DateOnly.FromDateTime(now.DateTime);

        var enabled = await _policy.GetBoolAsync(PolicyKeys.ProactiveEnabled, true);

        // 今天被赦免过：整整天都不再开口。
        // 走 Decider 的 Enabled 而不是单独 return，是为了让状态文字说清是哪一条挡住的。
        var snoozed = await _policy.GetRawAsync(PolicyKeys.ProactiveSnoozed);
        var isSnoozed = string.Equals(snoozed, today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            StringComparison.Ordinal);

        var fired = new HashSet<ProactiveKind>();
        foreach (var kind in new[] { ProactiveKind.Morning, ProactiveKind.Evening, ProactiveKind.LowRate })
        {
            var raw = await _policy.GetRawAsync(FiredKey(kind, today));
            if (raw == "1")
            {
                fired.Add(kind);
            }
        }

        // 下一次"日程前提醒"要排除已经提醒过的。
        var items = await _items.GetDayListAsync(today);
        var upcoming = new List<ProactiveUpcoming>();
        foreach (var it in items.Where(x => x.StartAt is not null && x.Status == ItemStatus.Pending))
        {
            var already = await _policy.GetRawAsync(BeforeKey(it.Id));
            if (already == "1")
            {
                continue;
            }

            upcoming.Add(new ProactiveUpcoming(it.Id, it.Title, it.MinimalStep, it.StartAt!.Value));
        }

        // 最近几天的完成情况：判断"是不是一直很低"。
        var recent = new List<ProactiveDayRatio>();
        for (var i = 3; i >= 1; i--)
        {
            var d = today.AddDays(-i);
            var s = await _items.GetDayStatsAsync(d);
            recent.Add(new ProactiveDayRatio(d, s.Total, s.Done));
        }

        var lastAt = await ReadLastAtAsync();

        var decision = ProactiveDecider.Decide(new ProactiveContext
        {
            Now = now,
            Enabled = enabled && !isSnoozed,
            FiredToday = fired,
            LastFiredAt = lastAt,
            Upcoming = upcoming,
            RecentDays = recent
        });

        if (!decision.ShouldSpeak)
        {
            SetStatus(describeSkip(decision.Reason, isSnoozed));
            return;
        }

        await SpeakAsync(decision, today);

        // 局部函数：把"为什么不说"写成一句人话。
        static string describeSkip(ProactiveSkipReason reason, bool snoozed) => reason switch
        {
            ProactiveSkipReason.Disabled when snoozed => "今天你说过别再提了，它闭嘴了",
            ProactiveSkipReason.Disabled => "主动开口已关闭",
            ProactiveSkipReason.CoolingDown => "刚说过，冷却中",
            _ => "这一刻没什么要说的"
        };
    }

    /// <summary>
    /// 让 AI 说一句并送出去。
    /// </summary>
    /// <param name="markAsFired">是否记下"今天说过了"。手动测试时传 false。</param>
    public async Task<string> SpeakNowAsync(ProactiveKind kind, Guid? itemId = null, bool markAsFired = true)
    {
        var today = DateOnly.FromDateTime(DateTime.Now);
        return await SpeakAsync(new ProactiveDecision(kind, itemId, ProactiveSkipReason.None), today, markAsFired);
    }

    private async Task<string> SpeakAsync(ProactiveDecision decision, DateOnly today, bool markAsFired = true)
    {
        var kind = decision.Kind;

        // 后端不在就拉起来。这一步是必要的：开机自启后如果一直没打开对话页，
        // 后端可能还没起来（虽然启动时会预热一次，但它也可能已经崩了）。
        var health = await _guard.EnsureAsync();

        if (!health.Ok)
        {
            SetStatus($"想说话但连不上后端：{health.ErrorMessage}");
            await MarkAttemptedAsync(markAsFired, false, kind, today, decision.ItemId);
            return string.Empty;
        }

        // 密钥改成界面填之后，服务端那份是刻意留空的；客户端填的那把
        // 会随下面的请求带上，同样算就绪。这里拦错一次的代价是
        // "该开口的晚上一次都没说"，所以要宽松而不是严格。
        if (!health.AiReady && !_ai.HasKey)
        {
            SetStatus("后端在，但 AI 密钥还没填（时间页「AI 接入」）");
            await MarkAttemptedAsync(markAsFired, false, kind, today, decision.ItemId);
            return string.Empty;
        }

        SetStatus($"正在想该怎么说（{KindName(kind)}）……");

        var reply = await _api.ProactiveAsync(
            kind.ToString().ToLowerInvariant(), decision.ItemId, today);

        if (!reply.Ok || string.IsNullOrWhiteSpace(reply.Text))
        {
            // 【为什么失败也要记一次冷却】
            // 不记的话，下一分钟又来一次，而模型本来就慢，
            // 于是每分钟都发起一次几十秒的请求——那是在给自己制造卡顿。
            // 记冷却但不记"说过了"：过一阵子还会再试，今天这次不算数。
            SetStatus(string.IsNullOrWhiteSpace(reply.Error)
                ? "AI 这次没说出话来，过会儿再试"
                : $"AI 没说出话来：{reply.Error}");
            await MarkAttemptedAsync(markAsFired, false, kind, today, decision.ItemId);
            return string.Empty;
        }

        // ---------- 写进对话 ----------
        //
        // 【为什么先写库再弹通知】
        // 通知是给用户看的，对话是给用户回的。万一写库失败，
        // 至少不该出现"通知弹了、打开对话页却找不到这句话"——
        // 那会让人以为自己记错了。所以先写，写失败就整轮放弃。
        try
        {
            await _chat.AddAsync(ChatMessage.RoleAssistant, reply.Text);
        }
        catch (Exception ex)
        {
            CrashLogger.Write("保存 AI 主动说的话", ex);
            SetStatus("话说出来了，但没能存进对话");
            await MarkAttemptedAsync(markAsFired, false, kind, today, decision.ItemId);
            return string.Empty;
        }

        // 通知上的标题用 AI 给的，没有就退回场合名。
        var title = string.IsNullOrWhiteSpace(reply.Title) ? KindName(kind) : reply.Title;
        _notifier?.Show(NoticePrefix + kind.ToString().ToLowerInvariant(), title, reply.Text);

        // 通知对话页：跨 Tab 刷新不能依赖 OnAppearing（Shell 切 Tab 不保证触发）。
        WeakReferenceMessenger.Default.Send(new ProactiveSpokeMessage());

        SetStatus($"刚说了（{KindName(kind)}）：{TrimOneLine(reply.Text)}");

        await MarkAttemptedAsync(markAsFired, true, kind, today, decision.ItemId);
        return reply.Text;
    }

    /// <summary>
    /// 记下这一轮的结果。
    /// </summary>
    /// <param name="markAsFired">手动测试时为 false：不写"今天说过了"，也不写冷却。</param>
    /// <param name="succeeded">是否真的说成了。</param>
    private async Task MarkAttemptedAsync(
        bool markAsFired, bool succeeded, ProactiveKind kind, DateOnly today, Guid? itemId)
    {
        if (!markAsFired)
        {
            return;
        }

        // 冷却：不管成败都记。失败也记是为了不让它每分钟重试一次（见上面的注释）。
        await _policy.SetAsync(PolicyKeys.ProactiveLastAt,
            DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
            PolicySource.User);

        if (!succeeded)
        {
            return;
        }

        // 「日程前提醒」按条目记，其余按场合 + 日期记一天一次。
        if (kind == ProactiveKind.Before && itemId is { } id)
        {
            await _policy.SetAsync(BeforeKey(id), "1", PolicySource.User);
            return;
        }

        await _policy.SetAsync(FiredKey(kind, today), "1", PolicySource.User);
    }

    /// <summary>
    /// 通知上的按钮被点了。
    ///
    /// 【为什么只处理「今天别再提了」】
    /// 「知道了」是这次到此为止，不需要任何动作——通知自己会收起来。
    /// 而赦免必须落库：它是跨重启生效的承诺，用户按过就必须算数。
    /// </summary>
    private void OnNotifierInvoked(string appId, NoticeAction action)
    {
        if (action != NoticeAction.Snooze || !appId.StartsWith(NoticePrefix, StringComparison.Ordinal))
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await _policy.SetAsync(PolicyKeys.ProactiveSnoozed,
                    DateOnly.FromDateTime(DateTime.Now).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                    PolicySource.User);
                SetStatus("今天你说过别再提了，它闭嘴了");
            }
            catch (Exception ex)
            {
                CrashLogger.Write("记录主动开口赦免", ex);
            }
        });
    }

    private async Task<DateTimeOffset?> ReadLastAtAsync()
    {
        var raw = await _policy.GetRawAsync(PolicyKeys.ProactiveLastAt);

        return DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeLocal, out var parsed)
            ? parsed
            : null;
    }

    private static string FiredKey(ProactiveKind kind, DateOnly day) =>
        PolicyKeys.ProactiveFiredPrefix + kind.ToString().ToLowerInvariant() + "." +
        day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static string BeforeKey(Guid itemId) =>
        PolicyKeys.ProactiveBeforePrefix + itemId.ToString("N");

    private void SetStatus(string text)
    {
        StatusText = text;
        StatusChanged?.Invoke();
    }

    private static string TrimOneLine(string text)
    {
        var one = text.Replace('\n', ' ').Replace('\r', ' ').Trim();
        return one.Length > 40 ? one[..40] + "…" : one;
    }

    /// <summary>场合的中文名，用于通知标题和界面。</summary>
    public static string KindName(ProactiveKind kind) => kind switch
    {
        ProactiveKind.Morning => "早晨计划",
        ProactiveKind.Before => "日程前提醒",
        ProactiveKind.Evening => "晚间总结",
        ProactiveKind.LowRate => "最近有点卡",
        // 娱乐干预目前不走这个服务（它有自己的通知通道），
        // 但名字先写死在这：万一以后合并，标题不会退化成"主动开口"。
        ProactiveKind.Nudge => "娱乐干预",
        _ => "主动开口"
    };

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _timer?.Dispose();
        _timer = null;

        if (_notifier is not null)
        {
            _notifier.Invoked -= OnNotifierInvoked;
        }
    }
}
