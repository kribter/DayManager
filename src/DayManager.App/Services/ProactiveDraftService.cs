using System.Collections.Concurrent;
using DayManager.Core.Entities;
using DayManager.Core.Proactive;

namespace DayManager.App.Services;

/// <summary>
/// 提前向 AI 要一句话存着，到点取用。
///
/// 【为什么这个东西要单独存在】
/// 有两处要在"某个时刻突然开口"：
///   娱乐干预——他刷了太久，要抢一句让他动起来；
///   专注到点——一轮「先做 10 分钟」跑完，要一句收尾。
/// 两者的节奏是一模一样的：**到点之前先去要，到点直接用**。
/// 以前这套预备逻辑只写在干预引擎里，专注那边的文案是写死的模板。
/// 于是同一个软件里出现了两种"它主动说的话"：一种会看今天的清单、会说人话，
/// 另一种永远是同一句。分开写两份，改了一边另一边不动是迟早的事。
/// 抽到这一处，两边都只剩"什么时候要、什么时候用"。
///
/// 【为什么要提前，而不是到点现要】
/// AI 说一句要十几秒。到点再开口，通知会晚；更糟的是 AI 一慢
/// （限流、重试一次）就只能用本地模板顶上——那这一整条链路等于没接。
/// 提前一分钟开口，这段富余正好用来吸收抖动。
///
/// 【为什么它必须是"草稿"而不是"就这句了"】
/// 毕竟隔了将近一分钟才弹出去。中间他可能已经把那件事做完、
/// 也可能那条日程刚被改过。草稿只在这两种情形之外才算数（见 <c>Take</c>）。
/// </summary>
public sealed class ProactiveDraftService : IDisposable
{
    private readonly ApiClient _api;
    private readonly BackendGuard _guard;
    private readonly AiSettings _ai;

    private readonly ConcurrentDictionary<string, Draft> _drafts =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>正在向 AI 要话的对象，防重复请求。</summary>
    private readonly ConcurrentDictionary<string, bool> _preparing =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly CancellationTokenSource _cts = new();

    private bool _disposed;

    public ProactiveDraftService(ApiClient api, BackendGuard guard, AiSettings ai)
    {
        _api = api;
        _guard = guard;
        _ai = ai;
    }

    /// <summary>提前多久开口向 AI 要这句话。</summary>
    public const int WarmupMinutes = 1;

    /// <summary>
    /// 一句话备好之后多久作废。
    ///
    /// 【为什么还要有个上限】
    /// 正常情况草稿几十秒内就用掉了。留着兜的是那种要用却没用上的情形：
    /// 比如他在最后一分钟切走了应用，几十分钟后又回来接着刷。
    /// 那时候那句话描述的已经是几十分钟前的局面了，宁成本高也不用。
    /// </summary>
    public const int DraftMaxMinutes = 5;

    /// <summary>
    /// 提前向 AI 要一句话。**不阻塞**：请求在后台跑，到点用 <see cref="Take"/> 取。
    /// </summary>
    /// <param name="key">这份草稿归谁。娱乐干预用应用 id，专注计时用条目 id。</param>
    /// <param name="kind">场合，决定后端拼哪一份提示词。</param>
    /// <param name="itemId">场合为 focus 时，是正在做的那一条。</param>
    /// <param name="today">"今天"是哪天。</param>
    /// <param name="subject">现场的主角：娱乐干预是应用名，专注计时是日程标题。</param>
    /// <param name="minutes">已经过去的分钟数（娱乐干预是这一段刷了多久，专注是这一轮做了多久）。</param>
    /// <param name="thresholdMinutes">到说话的阈值。娱乐干预是阈值设置，专注是这一轮的总长。</param>
    public void WarmUp(
        string key,
        ProactiveKind kind,
        Guid? itemId,
        DateOnly today,
        string subject,
        int minutes,
        int thresholdMinutes)
    {
        // 早了就别要：他多半马上就切走了，这一问白花钱。
        if (minutes < thresholdMinutes - WarmupMinutes)
        {
            return;
        }

        // 已经备好了、或者上一次请求还在路上，都不再要一次。
        if (_drafts.ContainsKey(key) || !_preparing.TryAdd(key, true))
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                var health = await _guard.EnsureAsync(cancellationToken: _cts.Token);

                // 后端不在、或者密钥还没填：拿不到就拿不到，
                // 取用的时候会退回本地那句模板，这里不用让它知道。
                if (!health.Ok || (!health.AiReady && !_ai.HasKey))
                {
                    return;
                }

                var reply = kind == ProactiveKind.Focus
                    ? await _api.ProactiveAsync(
                        kind.ToString().ToLowerInvariant(), itemId, today,
                        focus: new FocusFinishRequest(subject, minutes),
                        cancellationToken: _cts.Token)
                    : await _api.ProactiveAsync(
                        kind.ToString().ToLowerInvariant(), itemId, today,
                        nudge: new NudgeRequest(subject, minutes),
                        cancellationToken: _cts.Token);

                if (reply.Ok && !string.IsNullOrWhiteSpace(reply.Text))
                {
                    _drafts[key] = new Draft(reply.Text, reply.ItemId, DateTimeOffset.Now);
                }
            }
            catch (Exception ex)
            {
                // 拿不到就不拿：下面有本地模板顶着，这一分钟的失败不值得让他知道。
                CrashLogger.Write("预热主动开口文案", ex);
            }
            finally
            {
                _preparing.TryRemove(key, out _);
            }
        }, _cts.Token);
    }

    /// <summary>
    /// 取走提前备好的那句话。没备好、过了保质期、或者它点名的那件事已经不在清单里，都作废。
    ///
    /// 【为什么最后一个条件不能省】
    /// 草稿是几十秒前写的。这中间他可能已经把那件做完了，也可能刚把它删了。
    /// 这时候弹出一句"先做 XXX"是在让他去做一件刚做完的事——
    /// 这种提醒出现两三次，整个功能就可以关掉了。
    /// </summary>
    public string? Take(string key, IReadOnlyList<Item> remaining)
    {
        if (!_drafts.TryRemove(key, out var draft))
        {
            return null;
        }

        if (DateTimeOffset.Now - draft.CreatedAt > TimeSpan.FromMinutes(DraftMaxMinutes))
        {
            return null;
        }

        if (draft.ItemId is { } id && remaining.All(x => x.Id != id))
        {
            return null;
        }

        return draft.Text;
    }

    /// <summary>提前备好的一句话，以及它点的是哪一件（可以为空）。</summary>
    private sealed record Draft(string Text, Guid? ItemId, DateTimeOffset CreatedAt);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        // 还在半路上的那次 AI 请求一并掐掉：服务都要停了，
        // 它的结果也没人用了（草稿只能落到已经 Dispose 的对象手里）。
        _cts.Cancel();
        _cts.Dispose();
    }
}
