using CommunityToolkit.Mvvm.ComponentModel;
using DayManager.Core.Entities;
using DayManager.Core.Enums;
using DayManager.Core.Intervention;
using DayManager.Data.Repositories;
using Microsoft.Maui.Dispatching;

namespace DayManager.App.Services;

/// <summary>
/// 「先做 10 分钟」的计时器。
///
/// 【为什么要有这个东西】
/// 这个软件要解决的问题不是"忘了做什么"，而是"知道该做但启动不了"。
/// 遗忘可以用提醒解决，启动不了不能用——把待办推到眼前只会让它更吓人。
/// "先做 10 分钟"把承诺从"做完这件事"降到"做十分钟"，
/// 而人真正卡住的从来不是后面的两小时，是最开始那一下。
///
/// 【为什么它是单例、并且要挂在 DI 里，而不是写在某个页面里】
/// 计时一旦开始，用户很可能会去翻大任务页、去看时间页、甚至把窗口藏进托盘。
/// 写在页面里的计时器会跟着页面一起没了（页面对象被回收或重新创建），
/// 表现为"我明明点了开始，回来它已经消失了"。
/// 常驻在 DI 里，它就跟采集器、干预引擎一样，是程序的一部分而不是某个界面的一部分。
///
/// 【为什么计时器只在内存里，不进数据库】
/// 一期要验证的是"这个动作有没有用"，不是"统计我坚持了多久"。
/// 落库要加一张表、一份迁移、还要决定它跟 usage_sessions 怎么区分，
/// 这些代价现在都不值得付。代价认下来：**关掉程序，正在进行的这一轮就没了**——
/// 而程序本来就是常驻的（托盘），这一轮真正会丢的场景只有"重启电脑"。
///
/// 【通知为什么也走 IInterventionNotifier】
/// 它不是"干预提醒"，但用的是同一条系统通道：不点不走的那一种。
/// 普通 toast 会自己滑走，而正反馈滑走了等于没发生。
/// </summary>
public sealed partial class FocusSessionService : ObservableObject, IDisposable
{
    private readonly ItemRepository _items;
    private readonly IInterventionNotifier _notifier;

    private IDispatcherTimer? _timer;
    private DateTimeOffset _endsAt;
    private int _minutes;

    public FocusSessionService(ItemRepository items, IInterventionNotifier notifier)
    {
        _items = items;
        _notifier = notifier;
    }

    /// <summary>
    /// 一轮的时长。写死 10 分钟，不做设置项。
    ///
    /// 【为什么不给一个可以调的数字】
    /// "先做 10 分钟"这句话本身就是它的全部价值——承诺小到不好意思拒绝。
    /// 给一个可调的数字，人就会开始盘算"是不是 15 分钟更划算"，
    /// 而盘算本身就是启动成本。真要改，改这一个常量。
    /// </summary>
    public const int DefaultMinutes = 10;

    /// <summary>通知的标识。撤掉还挂着的那一条时用它。</summary>
    private const string NoticeAppId = "focus";

    /// <summary>有没有一轮正在进行（包括已经到点、正在等用户收尾）。</summary>
    [ObservableProperty]
    public partial bool IsActive { get; set; }

    /// <summary>
    /// 时间到了、正在等用户决定（继续 / 收工）。
    ///
    /// 【为什么"到点"不等于"结束"】
    /// 到点之后人还在做，也可能已经做完正在兴头上。
    /// 这时候程序替他决定"这轮结束了"是粗暴的，
    /// 所以到点只是弹出来问一句，决定权留给他。
    /// </summary>
    [ObservableProperty]
    public partial bool IsFinished { get; set; }

    [ObservableProperty]
    public partial Guid ItemId { get; set; }

    [ObservableProperty]
    public partial string ItemTitle { get; set; } = string.Empty;

    /// <summary>倒计时文字，形如 "9:58"。</summary>
    [ObservableProperty]
    public partial string RemainingText { get; set; } = string.Empty;

    /// <summary>到点以后说的那句话。先给正反馈，再给下一步。</summary>
    [ObservableProperty]
    public partial string PromptText { get; set; } = string.Empty;

    /// <summary>
    /// 开始一轮。
    /// </summary>
    /// <returns>状态被改成"进行中"之后的那条记录，调用方拿它刷新界面。</returns>
    public async Task<Item?> StartAsync(Guid id, string title, int minutes = DefaultMinutes)
    {
        StopTimer();

        //
        // 【为什么开始计时就要把任务置成"进行中"】
        // 点这个按钮的动作本身，就是"我开始做了"最真的一次表达——
        // 比勾选"进行中"菜单可信得多，因为它是带着承诺来的。
        // 而 StartedAt 一旦写下，"从想到做到花了多久"（启动延迟）就有据可查；
        // 这个值正是后面判断"启动困难有没有改善"的唯一依据。
        // 重复点不会把它冲掉（仓储里用的是 ??=），所以连续几轮也只记第一次。
        //
        var updated = await _items.SetStatusAsync(id, ItemStatus.InProgress);

        _minutes = minutes;
        _endsAt = DateTimeOffset.Now.AddMinutes(minutes);

        ItemId = id;
        ItemTitle = title;
        IsFinished = false;
        IsActive = true;
        UpdateRemainingText();

        // 【为什么用 Dispatcher 的定时器而不是 System.Threading.Timer】
        // 后者在后台线程触发，改绑定属性要自己切回 UI 线程，
        // 忘了切就是"每过一秒崩一次"或者"数字不动"（见时间页踩过的那一次）。
        // Dispatcher 定时器直接在 UI 线程上跳，改属性天然安全。
        _timer = Application.Current?.Dispatcher.CreateTimer();
        if (_timer is not null)
        {
            _timer.Interval = TimeSpan.FromSeconds(1);
            _timer.Tick += OnTick;
            _timer.Start();
        }

        return updated;
    }

    /// <summary>
    /// 再续一轮。
    ///
    /// 【为什么续的时候不加倍、不记住已经做了几轮】
    /// 每多一个数字就多一个可以纠结的地方。一轮就是一轮，接着做就再来一轮。
    /// </summary>
    public async Task ContinueAsync()
    {
        IsFinished = false;
        await StartAsync(ItemId, ItemTitle, _minutes > 0 ? _minutes : DefaultMinutes);
    }

    /// <summary>提前结束（到点前主动停，或者用户在收尾时点了「今天先到这」）。</summary>
    public void End()
    {
        StopTimer();

        IsActive = false;
        IsFinished = false;
        RemainingText = string.Empty;

        // 用户已经处理过这件事了，还挂在通知中心里的那条就该撤掉，
        // 否则它会一直待在那儿，变成"我好像还有什么没做完"的错觉。
        _notifier.Clear(NoticeAppId);
    }

    /// <summary>
    /// 现在就到点（用户在计时中又点了一下那条任务）。
    ///
    /// 【为什么再点一下是"结束"而不是"重新开始"】
    /// 计时中的那条按钮还写着「10 分钟」，再点一次合理的期待是"让它停下"，
    /// 而不是"又来一轮"——后者会让按钮的含义随着状态漂移，点下去之前得先想。
    /// </summary>
    public void FinishNow()
    {
        Finish();
    }

    private void OnTick(object? sender, EventArgs e)
    {
        var remaining = _endsAt - DateTimeOffset.Now;

        if (remaining > TimeSpan.Zero)
        {
            UpdateRemainingText(remaining);
            return;
        }

        Finish();
    }

    private void Finish()
    {
        StopTimer();

        RemainingText = "0:00";
        IsFinished = true;

        //
        // 【到点说什么，是这个功能的成败所在】
        // 绝对不能写成"还差多少没做完"——那会让这 10 分钟变成一次考核。
        // 顺序固定为：**先承认已经做了，再给下一步，而且"没做完"也要算做到了**。
        // 人最难的是开头，开头过了就是真的过了。
        //
        PromptText = $"「{ItemTitle}」已经做了 {_minutes} 分钟。"
                     + "做完了就去打个勾，没做完也已经开了头 —— 最难的就是开头那一下。";

        _notifier.Show(
            NoticeAppId,
            $"{_minutes} 分钟到了",
            $"{ItemTitle} —— 做完了就去打个勾");
    }

    private void UpdateRemainingText()
    {
        UpdateRemainingText(_endsAt - DateTimeOffset.Now);
    }

    private void UpdateRemainingText(TimeSpan remaining)
    {
        if (remaining < TimeSpan.Zero)
        {
            remaining = TimeSpan.Zero;
        }

        RemainingText = $"{(int)remaining.TotalMinutes}:{remaining.Seconds:00}";
    }

    private void StopTimer()
    {
        if (_timer is null)
        {
            return;
        }

        _timer.Tick -= OnTick;
        _timer.Stop();
        _timer = null;
    }

    public void Dispose() => StopTimer();
}
