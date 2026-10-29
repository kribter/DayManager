using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DayManager.App.Services;
using DayManager.Core.Ai;
using DayManager.Core.Enums;
using DayManager.Core.Intervention;
using DayManager.Data.Repositories;

namespace DayManager.App.ViewModels;

/// <summary>时间页看的是哪一段。</summary>
public enum UsageSpan
{
    Day,
    Week,
    Month,
    Year
}

/// <summary>
/// 时间页的大脑：使用统计。
///
/// 【这一页要回答的问题，以及刻意没做的事】
/// 要回答的是"我的时间到底去哪了"，顺带让人看到自己几点最在状态。
/// 刻意没做排行榜式的比较、没有"你比昨天差"这类文案——
/// 这个软件的用户是来对抗逃避的，不是来被一个统计页面再骂一遍的。
/// 所以：数字照实给，评价不给。
///
/// 【为什么四个跨度共用一套加载逻辑】
/// 只是区间长短不同。各写一套的话，切换跨度时数字对不上（日视图 3 小时、
/// 周视图里那天 2.5 小时），而没人能看出是哪一套错了。见 UsageRepository.GetRangeAsync。
/// </summary>
public partial class TimeViewModel : ObservableObject
{
    private readonly UsageRepository _usage;
    private readonly PolicyRepository _policy;
    private readonly AppRuleRepository _rules;
    private readonly AiSettings _ai;
    private readonly ApiClient? _api;

    /// <summary>
    /// 干预引擎。**可空**：它依赖采集器和系统通知，而这两样只有 Windows 端有。
    /// Android 早期还没有这一块，构造时注入 null，界面上的设置区就不显示。
    ///
    /// 【为什么不抽一个接口出来】
    /// 这一页只用到它的两样东西：一句话状态、一个"发测试通知"的按钮。
    /// 为一个只读属性 + 一个按钮抽接口，多出来的抽象比省下的代码还多。
    /// 等 Android 真的要做时再抽，那时才知道两边共同的部分到底是什么。
    /// </summary>
    private readonly InterventionService? _intervention;

    /// <summary>
    /// 托盘（把窗口藏起来但让程序继续跑）与开机自启。
    ///
    /// 【为什么这两个要用可空 + 接口】
    /// 和干预引擎一样的处境：它们是 Windows 特有的能力。
    /// Android 上没有托盘这回事，构造时注入 null，这一块设置就不显示——
    /// 比在界面层写一堆平台判断干净。
    /// </summary>
    private readonly IWindowShell? _shell;
    private readonly IAutoStartService? _autoStart;

    /// <summary>
    /// AI 主动开口。可空：它要连后端和模型，Android 端早期没有。
    /// 为 null 时这一块设置不显示。
    /// </summary>
    private readonly ProactiveNudgeService? _proactive;

    /// <summary>界面深浅。每个平台都有，不做成可空。</summary>
    private readonly ThemeService _theme;

    /// <summary>
    /// 自动刷新间隔。
    ///
    /// 【为什么是 20 秒，而不是跟着采集的 5 秒】
    /// 采集每 5 秒写一次库，但刷新一次要重新聚合一整天的数据、再按小时摊一遍。
    /// 5 秒刷一次人眼也看不出差别，只是白白多跑几倍查询。
    /// 20 秒是"切完应用回头就能看见数字动了"和"别让电脑一直算"之间的平衡。
    /// </summary>
    private static readonly TimeSpan AutoRefreshInterval = TimeSpan.FromSeconds(20);

    /// <summary>同一时刻只允许一次刷新在跑（翻页点快了、自动刷新撞上手动刷新都会撞车）。</summary>
    private readonly SemaphoreSlim _refreshGate = new(1, 1);

    private readonly CancellationTokenSource _autoRefreshCts = new();
    private bool _autoRefreshStarted;

    /// <summary>当前看的是哪一天。周/月/年都从它推出来。</summary>
    private DateOnly _anchor = DateOnly.FromDateTime(DateTime.Today);

    public TimeViewModel(
        UsageRepository usage,
        PolicyRepository policy,
        AppRuleRepository rules,
        InterventionService? intervention = null,
        IWindowShell? shell = null,
        IAutoStartService? autoStart = null,
        ProactiveNudgeService? proactive = null,
        ThemeService? theme = null,
        AiSettings? ai = null,
        ApiClient? api = null)
    {
        _usage = usage;
        _policy = policy;
        _rules = rules;
        _intervention = intervention;
        _shell = shell;
        _autoStart = autoStart;
        _proactive = proactive;
        _theme = theme ?? new ThemeService(policy);
        _ai = ai ?? AiSettings.Load();
        _api = api;

        if (_proactive is not null)
        {
            // 服务那边状态一变就同步过来。它是定时跑的，
            // 平时完全静默——界面上没有这句话，用户没法区分"还没到点儿"和"坏了"。
            _proactive.StatusChanged += () =>
                MainThread.BeginInvokeOnMainThread(() =>
                {
                    ProactiveStatusText = _proactive.StatusText;
                    OnPropertyChanged(nameof(ProactiveStatusText));
                });
        }
    }

    /// <summary>当前在用的规则（进程名 → 归类）。标记应用之后要重查一次列表，所以留一份。</summary>
    private Dictionary<string, AppKind> _kindByApp = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 开始自动刷新（幂等，重复调用没有副作用）。
    ///
    /// 【为什么必须自己定时，不能只靠 OnAppearing】
    /// Shell 切 Tab 时**并不保证**触发 `OnAppearing`（M6 就栽过一次：
    /// 改完数据切回列表页还是旧的）。而采集是后台一直在跑的——
    /// 人停在时间页上什么都不点，数字也该自己长起来。
    /// 靠"什么时候会变"的约定去猜，不如定时重查：
    /// 一个人用的数据量，多查一次不值得省。
    /// </summary>
    public void StartAutoRefresh()
    {
        if (_autoRefreshStarted)
        {
            return;
        }

        _autoRefreshStarted = true;

        // 干预设置从库里读一次。放在这里而不是构造里：
        // 构造函数里不能 await，而用 _ = 硬起一个任务又会让"界面已经能点、设置还没到"中间那一下闪烁。
        _ = LoadPolicyAsync();

        _ = Task.Run(async () =>
        {
            while (!_autoRefreshCts.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(AutoRefreshInterval, _autoRefreshCts.Token);
                }
                catch (TaskCanceledException)
                {
                    return;
                }

                // 【为什么整个刷新都要切回主线程，而不是只把"改属性"那几行切过去】
                // 这里曾经直接在后台线程跑 RefreshAsync，结果界面纹丝不动：
                // MAUI 的 ObservableCollection 和绑定属性只能由 UI 线程改，
                // 后台线程改集合要么不渲染、要么当场抛异常——
                // 而 quiet 模式又把异常吞了（"自动刷新失败不该弹框"是对的），
                // 于是每次刷新都在无声地失败，看日志才找得到。
                // 顺带的取舍：把查库也放进主线程。单人用的 SQLite 查询是毫秒级，
                // 20 秒一次，不值得为它把"取数"和"上屏"拆成两半各管各的线程。
                try
                {
                    await MainThread.InvokeOnMainThreadAsync(() => RefreshAsync(quiet: true));
                }
                catch (Exception ex)
                {
                    // 调度本身失败（理论上只有线程模型被破坏才会走到这）也落日志，
                    // 别再让一次刷新无声无息地消失。
                    Services.CrashLogger.Write("时间页自动刷新调度失败", ex);
                }
            }
        });
    }

    /// <summary>停止自动刷新。目前只有程序退出才需要（页面和程序一样长命）。</summary>
    public void StopAutoRefresh() => _autoRefreshCts.Cancel();

    /// <summary>
    /// 刷新一次。`quiet` 表示"后台自动刷的"，出错也不弹框——
    /// 每 20 秒弹一个错误框，比数字暂时停留在旧值烦人得多。
    /// </summary>
    private async Task RefreshAsync(bool quiet)
    {
        // 已经有刷新在跑就直接跳过：这次错过没关系，20 秒后还有一轮。
        if (!await _refreshGate.WaitAsync(0))
        {
            return;
        }

        try
        {
            await LoadCoreAsync(quiet);
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    public ObservableCollection<AppUsageRow> Apps { get; } = new();

    [ObservableProperty]
    public partial UsageSpan Span { get; set; } = UsageSpan.Day;

    [ObservableProperty]
    public partial string RangeTitle { get; set; } = string.Empty;

    /// <summary>总时长，例如「3 小时 12 分」。</summary>
    [ObservableProperty]
    public partial string TotalText { get; set; } = "0 分钟";

    /// <summary>副标题：记录了几段、从几点到几点。</summary>
    [ObservableProperty]
    public partial string NoteText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string EntertainmentText { get; set; } = string.Empty;

    /// <summary>
    /// 娱乐时长是否展开。
    ///
    /// 【为什么默认收起来】
    /// 回顾界面的第一原则是正反馈优先（需求 2.9）。
    /// 一进来就把"你今天刷了两小时"摆在最显眼的位置，
    /// 结果是人不敢打开这一页——而不看的统计等于没做。
    /// 想看的人点一下就在，不用藏得更深。
    /// </summary>
    [ObservableProperty]
    public partial bool IsEntertainmentShown { get; set; }

    /// <summary>折叠按钮上的字。展开后才看得见具体数字。</summary>
    public string EntertainmentToggleText => IsEntertainmentShown ? "收起" : "看看娱乐时长";

    partial void OnIsEntertainmentShownChanged(bool value) =>
        OnPropertyChanged(nameof(EntertainmentToggleText));

    [ObservableProperty]
    public partial bool IsEmpty { get; set; } = true;

    [ObservableProperty]
    public partial bool HasApps { get; set; }

    // ================= 干预设置（M9） =================

    /// <summary>
    /// 干预引擎本身，直接暴露给界面绑它的状态文字。
    ///
    /// 【为什么把服务对象直接摆出来，而不是每个字段都抄一份到 ViewModel】
    /// 引擎每 60 秒会自己改 StatusText。抄一份就得订阅事件、切线程、再赋值，
    /// 抄三行代码换来的只是"ViewModel 里看起来干净一点"。
    /// 而状态文字本来就是"引擎此刻在想什么"，直接绑过去，说的和做的永远一致。
    /// </summary>
    public InterventionService? Intervention => _intervention;

    /// <summary>娱乐超时提醒开没开。</summary>
    [ObservableProperty]
    public partial bool IsReminderOn { get; set; } = true;

    /// <summary>连续用多久开始提醒（分钟）。</summary>
    [ObservableProperty]
    public partial int ThresholdMinutes { get; set; } = 30;

    public string ThresholdText => $"连续用同一个娱乐应用超过 {ThresholdMinutes} 分钟，弹一条系统通知";

    /// <summary>AI 主动开口开没开。</summary>
    [ObservableProperty]
    public partial bool IsProactiveOn { get; set; } = true;

    /// <summary>它在想什么 / 最近说过什么。</summary>
    [ObservableProperty]
    public partial string ProactiveStatusText { get; set; } = "还没检查过";

    /// <summary>这一块只在有这个能力时显示（要能通知 + 能连后端）。</summary>
    public bool HasProactive => _proactive is not null;

    /// <summary>
    /// 四种场合、各自什么时候会说话。写死在这里，因为它就是当前的规则本身。
    /// </summary>
    public string ProactiveRuleText =>
        "早晨（8–11 点首次启动）说今天的计划；有开始时间的日程提前 10 分钟提醒；"
        + "21 点后说一句晚间总结；连着三天完成率不到三成时会来谈一次。"
        + "同一种一天只说一次，两次之间至少隔一小时。";

    /// <summary>
    /// 现在就让 AI 说一句。
    ///
    /// 【为什么要有这个按钮】
    /// 否则只能干等到晚上九点才知道它到底说得好不好。
    /// 一个"能不能用"的问题如果需要等十二小时才能验证，那就等于没法验证。
    ///
    /// 【为什么不算"今天说过了"】
    /// 它是测试，不该把今天真正的那次开口顶掉。
    /// </summary>
    [RelayCommand]
    private async Task TestProactiveAsync()
    {
        if (_proactive is null)
        {
            return;
        }

        ProactiveStatusText = "正在让 AI 想一句……";

        try
        {
            // 按当前时刻挑一个场合：不指定就不会挑，而挑出来的才接近真实效果。
            var kind = DateTime.Now.Hour is >= 8 and < 11
                ? DayManager.Core.Proactive.ProactiveKind.Morning
                : DateTime.Now.Hour >= 21
                    ? DayManager.Core.Proactive.ProactiveKind.Evening
                    : DayManager.Core.Proactive.ProactiveKind.LowRate;

            var text = await _proactive.SpeakNowAsync(kind, markAsFired: false);

            ProactiveStatusText = string.IsNullOrWhiteSpace(text)
                ? _proactive.StatusText
                : $"说了：{text}";
        }
        catch (Exception ex)
        {
            CrashLogger.Write("试一句主动开口", ex);
            ProactiveStatusText = $"没说出来：{ex.Message}";
        }
    }

    /// <summary>阈值档位的选中态。和顶部日/周/月/年那组按钮用的是同一套高亮做法。</summary>
    public bool IsThreshold15 => ThresholdMinutes == 15;

    public bool IsThreshold30 => ThresholdMinutes == 30;

    public bool IsThreshold45 => ThresholdMinutes == 45;

    public bool IsThreshold60 => ThresholdMinutes == 60;

    /// <summary>时间页上有干预设置区的前提：这个平台注册了干预引擎。</summary>
    public bool HasIntervention => _intervention is not null;

    // ================= 后台与开机（托盘） =================

    /// <summary>
    /// 时间页上有没有"后台与开机"这一块。
    ///
    /// 【为什么按能力显示而不是永远显示】
    /// 这一整块只有在真能操作托盘的平台上才有意义。
    /// 显示一堆灰掉的按钮，比不显示更难受——它暗示"这里有，只是你点了没反应"。
    /// </summary>
    public bool HasShellControls => _shell?.IsSupported == true;

    /// <summary>注册表里现在登记了没有。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AutoStartText))]
    public partial bool IsAutoStartOn { get; set; }

    /// <summary>改不了自启时要说清为什么，而不是让按钮看起来像坏了。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AutoStartText))]
    public partial bool IsAutoStartSupported { get; set; }

    public string AutoStartText =>
        IsAutoStartSupported
            ? (IsAutoStartOn ? "开机自启：开" : "开机自启：关")
            : "开机自启：当前不能设置";

    /// <summary>
    /// 点 X 时是藏起来还是真退出。
    ///
    /// 【为什么默认是"藏"】
    /// 这个软件要拦的是"打开电脑就开始刷"——那一刻没有人会先打开它。
    /// 默认藏起来意味着它不会因为你关过一次窗口就整天缺席；
    /// 想要它彻底停下来，从托盘菜单里退出，那是一个明确的动作。
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CloseToTrayText))]
    public partial bool IsCloseToTray { get; set; } = true;

    public string CloseToTrayText => IsCloseToTray ? "关闭窗口时：藏到托盘继续跑" : "关闭窗口时：真的退出";

    /// <summary>
    /// 登记 / 取消开机自启。
    ///
    /// 【为什么失败要弹框而不是静默】
    /// 写注册表失败（权限、被策略软件拦）不会自己好转，
    /// 而用户看到的是"我点了开，它又跳回关"——比任何时候都需要一句解释。
    /// </summary>
    [RelayCommand]
    private async Task ToggleAutoStartAsync()
    {
        if (_autoStart is null)
        {
            return;
        }

        try
        {
            if (!_autoStart.TrySetEnabled(!IsAutoStartOn, out var error))
            {
                await ShowMessageAsync("没改成", error);
                return;
            }

            IsAutoStartOn = _autoStart.IsEnabled;
        }
        catch (Exception ex)
        {
            CrashLogger.Write("切换开机自启", ex);
        }
    }

    /// <summary>切换"X 是藏还是退"。立刻告诉托盘，不用等下一次刷新。</summary>
    [RelayCommand]
    private void ToggleCloseToTray()
    {
        IsCloseToTray = !IsCloseToTray;
        _shell?.SetCloseToTray(IsCloseToTray);

        _ = SavePolicyAsync(PolicyKeys.CloseToTray, IsCloseToTray ? "true" : "false");
    }

    /// <summary>
    /// 现在就把窗口藏起来——用来立刻验证托盘通不通，
    /// 不必先琢磨 X 按钮按下去会发生什么。
    /// </summary>
    [RelayCommand]
    private void HideToTray() => _shell?.HideToTray(notify: true);

    // ==================== 外观：深浅 ====================

    /// <summary>
    /// 三个选项做成一个三选一，而不是一个开关。
    ///
    /// 【为什么必须留"跟随系统"这一档】
    /// 只给"浅色 / 深色"两个按钮，等于让用户在两个极端之间二选一：
    /// 白天想要浅、晚上想要深的人，就得每天手动切两次，
    /// 而切两次的结果是第三天就不切了，界面停在他不想要的那档上。
    /// "跟随系统"是让系统替他做这件事——多数人的系统本来就是白天浅、晚上深。
    ///
    /// 【为什么用三个 bool 而不是一个字符串】
    /// 界面上要的是"这三个圆点哪个亮着"，三条互斥的布尔值直接对应三个控件，
    /// 比在 XAML 里写一堆字符串比较清楚。互斥的维护放在下面的 partial 里。
    /// </summary>
    [ObservableProperty]
    public partial bool IsThemeSystem { get; set; } = true;

    [ObservableProperty]
    public partial bool IsThemeLight { get; set; }

    [ObservableProperty]
    public partial bool IsThemeDark { get; set; }

    public string ThemeText => IsThemeDark ? "深色" : IsThemeLight ? "浅色" : "跟随系统";

    /// <summary>
    /// 正在把库里的选择点亮到界面上。
    /// 三个圆点是双向的：加载时点亮也会触发"值变了 → 保存 + 广播"。
    /// 那会把一次加载变成一次无谓的写库和全局重读。
    /// </summary>
    private bool _suppressThemeApply;

    partial void OnIsThemeSystemChanged(bool value)
    {
        if (value && !_suppressThemeApply)
        {
            _ = ApplyThemeAsync(ThemeService.System);
        }
    }

    partial void OnIsThemeLightChanged(bool value)
    {
        if (value && !_suppressThemeApply)
        {
            _ = ApplyThemeAsync(ThemeService.Light);
        }
    }

    partial void OnIsThemeDarkChanged(bool value)
    {
        if (value && !_suppressThemeApply)
        {
            _ = ApplyThemeAsync(ThemeService.Dark);
        }
    }

    /// <summary>
    /// 换主题并把选择存下来。
    ///
    /// 【为什么存库而不是只改内存】
    /// 这个软件是常驻后台、开机自启的——
    /// 只在内存里改，下一次开机启动又变回系统主题，
    /// 用户会以为自己上次没设置成功。
    /// </summary>
    private async Task ApplyThemeAsync(string mode)
    {
        try
        {
            await _theme.SetAsync(mode);
            OnPropertyChanged(nameof(ThemeText));
        }
        catch (Exception ex)
        {
            CrashLogger.Write("切换界面主题", ex);
        }
    }

    /// <summary>把某一档设成选中，其余两档清掉（只动选中态，不触发保存）。</summary>
    private void SelectThemeOnly(string mode)
    {
        _suppressThemeApply = true;
        IsThemeSystem = mode == ThemeService.System;
        IsThemeLight = mode == ThemeService.Light;
        IsThemeDark = mode == ThemeService.Dark;
        _suppressThemeApply = false;
    }

    // ==================== AI 怎么说话 ====================

    /// <summary>
    /// 三档语气，和主题那三个圆点同一套做法。
    ///
    /// 【为什么这三档的共同底线是"不许挖苦、不许空泛鼓励"】
    /// 这不是口味问题：为了对抗逃避而做的软件里，
    /// "你怎么又没做"和"加油你可以的"是同一种东西的两面——都是不看你究竟做了什么。
    /// 三档只调温度，不调这条底线。具体措辞见 <see cref="AiTone"/>。
    /// </summary>
    [ObservableProperty]
    public partial bool IsToneReserved { get; set; } = true;

    [ObservableProperty]
    public partial bool IsToneWarm { get; set; }

    [ObservableProperty]
    public partial bool IsToneDirect { get; set; }

    /// <summary>他自己写的那句人设。可空；写完输入框失焦就存。</summary>
    [ObservableProperty]
    public partial string AiPersona { get; set; } = string.Empty;

    private string CurrentTone =>
        IsToneWarm ? AiTone.Warm
        : IsToneDirect ? AiTone.Direct
        : AiTone.Reserved;

    public string ToneText => AiTone.DisplayName(CurrentTone);

    public string ToneHint => AiTone.Description(CurrentTone);

    private bool _suppressToneApply;

    partial void OnIsToneReservedChanged(bool value) => OnTonePicked(AiTone.Reserved, value);

    partial void OnIsToneWarmChanged(bool value) => OnTonePicked(AiTone.Warm, value);

    partial void OnIsToneDirectChanged(bool value) => OnTonePicked(AiTone.Direct, value);

    private void OnTonePicked(string tone, bool value)
    {
        OnPropertyChanged(nameof(ToneText));
        OnPropertyChanged(nameof(ToneHint));

        if (!value || _suppressToneApply)
        {
            return;
        }

        // 存库而不是只改内存：后端每次拼提示词时从这张表里读，
        // 不落库的话下一次开机（或后端重启）又变回克制档。
        _ = SavePolicyAsync(PolicyKeys.AiTone, tone);
    }

    private void SelectToneOnly(string tone)
    {
        _suppressToneApply = true;
        IsToneReserved = tone == AiTone.Reserved;
        IsToneWarm = tone == AiTone.Warm;
        IsToneDirect = tone == AiTone.Direct;
        _suppressToneApply = false;

        OnPropertyChanged(nameof(ToneText));
        OnPropertyChanged(nameof(ToneHint));
    }

    /// <summary>
    /// 存自定义那句人设。
    ///
    /// 【为什么是失焦才存，而不是每敲一个字存一次】
    /// 每敲一下写一次库，一行设置会变成几十行垃圾；
    /// 而输入框失焦正好是人打完一句话的自然时机，写一次就够。
    /// </summary>
    [RelayCommand]
    private async Task SavePersonaAsync()
    {
        await SavePolicyAsync(PolicyKeys.AiPersona, (AiPersona ?? string.Empty).Trim());
    }

    // ==================== AI 接入（密钥） ====================

    /// <summary>
    /// 密钥输入框里的内容。**注意它只在内存里和 Preferences（注册表）里**，
    /// 不会被写进任何随程序目录分发的配置文件。
    /// </summary>
    [ObservableProperty]
    public partial string AiKeyInput { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string AiBaseUrlInput { get; set; } = AiSettings.DefaultBaseUrl;

    [ObservableProperty]
    public partial string AiModelInput { get; set; } = AiSettings.DefaultModel;

    [ObservableProperty]
    public partial string AiStatusText { get; set; } = "还没填密钥";

    [ObservableProperty]
    public partial bool AiStatusOk { get; set; }

    [ObservableProperty]
    public partial bool IsTestingAi { get; set; }

    public string AiTestButtonText => IsTestingAi ? "正在试…" : "保存并试一下";

    /// <summary>把界面上的三行写回 Preferences。</summary>
    [RelayCommand]
    private void SaveAi()
    {
        _ai.ApiKey = (AiKeyInput ?? string.Empty).Trim();
        _ai.BaseUrl = (AiBaseUrlInput ?? string.Empty).Trim();
        _ai.Model = (AiModelInput ?? string.Empty).Trim();
        _ai.Save();

        AiStatusText = _ai.HasKey ? "已保存。点右边的按钮试一下能不能通。" : "密钥还没填。";
        AiStatusOk = false;
    }

    /// <summary>
    /// 真的发一条最小的请求，确认这条路走得通。
    ///
    /// 【为什么要真发一条，而不是只看"填了没有"】
    /// 密钥错一位也能填进去。填完以为能用、到真要用的时候才发现不对，
    /// 那次失败正好发生在他开口说正事的当下——最打击人的时机。
    /// 一个字的请求只花几分钱，换"填完就知道能不能用"，值。
    /// </summary>
    [RelayCommand]
    private async Task TestAiAsync()
    {
        SaveAi();

        if (_api is null)
        {
            AiStatusText = "后端还没连上，稍等几秒再试。";
            return;
        }

        if (!_ai.HasKey)
        {
            AiStatusText = "先填密钥再试。";
            return;
        }

        IsTestingAi = true;
        AiStatusText = "正在试…";
        OnPropertyChanged(nameof(AiTestButtonText));

        try
        {
            var reply = await _api.ChatAsync(
                new List<ChatMessageDto> { new() { Role = "user", Content = "只回一个字：好" } },
                jsonMode: false,
                maxTokens: 8);

            AiStatusOk = reply.Success;
            AiStatusText = reply.Success
                ? $"通了（{reply.Model ?? "已连接"}）。回对话页说句话试试。"
                : reply.ErrorMessage ?? "没通过，看看密钥对不对。";
        }
        catch (Exception ex)
        {
            AiStatusOk = false;
            AiStatusText = "连不上后端。";
            CrashLogger.Write("测试 AI 密钥", ex);
        }
        finally
        {
            IsTestingAi = false;
            OnPropertyChanged(nameof(AiTestButtonText));
        }
    }

    /// <summary>
    /// 正在把库里的值写进界面。
    ///
    /// 【为什么需要它】
    /// 开关是双向绑定：读库赋值时也会触发"值变了 → 保存"，于是刚读完就原样写回去一遍。
    /// 更糟的是自动刷新每 20 秒可能来一次，等于每 20 秒往库里写一行。
    /// 用一个标志把"这是加载"和"这是用户点的"分开，是处理双向绑定最简单可靠的办法。
    /// </summary>
    private bool _suppressPolicySave;

    /// <summary>干预设置只从库里读一次，之后由用户操作和引擎状态接管。</summary>
    private bool _policyLoaded;

    partial void OnIsReminderOnChanged(bool value)
    {
        if (_suppressPolicySave)
        {
            return;
        }

        _ = SavePolicyAsync(PolicyKeys.InterventionEnabled, value ? "true" : "false");
    }

    partial void OnIsProactiveOnChanged(bool value)
    {
        if (_suppressPolicySave)
        {
            return;
        }

        _ = SavePolicyAsync(PolicyKeys.ProactiveEnabled, value ? "true" : "false");
    }

    partial void OnThresholdMinutesChanged(int value)
    {
        OnPropertyChanged(nameof(ThresholdText));
        OnPropertyChanged(nameof(IsThreshold15));
        OnPropertyChanged(nameof(IsThreshold30));
        OnPropertyChanged(nameof(IsThreshold45));
        OnPropertyChanged(nameof(IsThreshold60));

        if (_suppressPolicySave)
        {
            return;
        }

        _ = SavePolicyAsync(PolicyKeys.ThresholdMinutes, value.ToString());
    }

    private async Task SavePolicyAsync(string key, string value)
    {
        try
        {
            // Source 写 User：需求 2.7 定了用户手动改过的参数 AI 不得再自动覆盖。
            await _policy.SetAsync(key, value, PolicySource.User);
        }
        catch (Exception ex)
        {
            CrashLogger.Write("保存干预设置", ex);
        }
    }

    /// <summary>从库里读一次干预设置。</summary>
    private async Task LoadPolicyAsync()
    {
        if (_policyLoaded)
        {
            return;
        }

        _policyLoaded = true;

        try
        {
            var enabled = await _policy.GetBoolAsync(PolicyKeys.InterventionEnabled, true);
            var threshold = await _policy.GetIntAsync(PolicyKeys.ThresholdMinutes, 30);
            var closeToTray = await _policy.GetBoolAsync(PolicyKeys.CloseToTray, true);
            var proactive = await _policy.GetBoolAsync(PolicyKeys.ProactiveEnabled, true);
            var tone = AiTone.Parse(await _policy.GetRawAsync(PolicyKeys.AiTone));
            var persona = await _policy.GetRawAsync(PolicyKeys.AiPersona);

            _suppressPolicySave = true;
            IsReminderOn = enabled;
            SelectThemeOnly(_theme.Mode);
            SelectToneOnly(tone);
            AiPersona = persona ?? string.Empty;

            // 密钥属于"连接设置"，不进 policy_settings：那张表在数据库里，
            // 而数据库是要能被拷给别人看数据的——密钥绝不能搭这趟车。
            AiKeyInput = _ai.ApiKey;
            AiBaseUrlInput = _ai.BaseUrl;
            AiModelInput = _ai.Model;
            AiStatusOk = _ai.HasKey;
            AiStatusText = _ai.HasKey
                ? $"已保存（{AiSettings.Mask(_ai.ApiKey)}）。回对话页说句话就是它在回答。"
                : "还没填密钥，AI 现在没法回答。";
            ThresholdMinutes = threshold;
            IsCloseToTray = closeToTray;
            IsProactiveOn = proactive;
            _suppressPolicySave = false;

            if (_proactive is not null)
            {
                ProactiveStatusText = _proactive.StatusText;
            }

            // 托盘那边马上要知道：它在任何界面操作之前就得按这个偏好拦住"关闭"。
            _shell?.SetCloseToTray(IsCloseToTray);

            if (_autoStart is not null)
            {
                IsAutoStartSupported = _autoStart.IsSupported;
                IsAutoStartOn = _autoStart.IsEnabled;
            }
        }
        catch (Exception ex)
        {
            CrashLogger.Write("读取干预设置", ex);
        }
    }

    /// <summary>点阈值档位。</summary>
    [RelayCommand]
    private void SetThreshold(string raw)
    {
        if (int.TryParse(raw, out var minutes) && minutes > 0 && minutes != ThresholdMinutes)
        {
            ThresholdMinutes = minutes;
        }
    }

    /// <summary>
    /// 把一个应用标成娱乐 / 工作 / 不标记。
    ///
    /// 【为什么这个入口必须在这儿，而不是只在对话里让 AI 改】
    /// 看到"B 站 1 小时 20 分"这一刻，才最可能想起"对了，这个得管一下"。
    /// 要他去对话页描述"把 B 站标成娱乐"，是把一个两次点击的动作变成一次输入。
    /// </summary>
    [RelayCommand]
    private async Task MarkAppAsync(AppUsageRow row)
    {
        if (row is null || !row.CanMark)
        {
            return;
        }

        var page = CurrentPage;
        if (page is null)
        {
            return;
        }

        var choice = await page.DisplayActionSheetAsync(
            row.Name, "取消", null, "算娱乐", "算工作", "不标记");

        AppKind? kind = choice switch
        {
            "算娱乐" => AppKind.Entertainment,
            "算工作" => AppKind.Work,
            "不标记" => AppKind.Neutral,
            _ => null
        };

        if (kind is null)
        {
            return;
        }

        try
        {
            await _rules.SetKindAsync(row.AppId, row.Name, kind.Value);

            // 重算一次这一页：徽章和娱乐时长都要跟着变。
            await LoadCoreAsync(quiet: true);
        }
        catch (Exception ex)
        {
            await ReportErrorAsync(ex);
        }
    }

    /// <summary>
    /// 发一条测试通知。
    ///
    /// 【为什么这个按钮值得摆出来】
    /// 系统通知能不能显示，取决于 Windows 的通知总开关、专注助手、以及应用有没有被静音。
    /// 这些都发生在程序外面——程序自己不弹一次，永远不知道自己是不是被系统吞了。
    /// 没有它，"为什么一条提醒都没有"要从三四个方向一个个排除。
    /// </summary>
    [RelayCommand]
    private async Task SendTestNoticeAsync()
    {
        if (_intervention?.SendTestNotice() == true)
        {
            return;
        }

        var page = CurrentPage;
        if (page is not null)
        {
            await page.DisplayAlertAsync(
                "没发出去",
                "系统通知被拦住了。到 Windows 设置 → 系统 → 通知里看看 DayManager 是不是被关掉或静音了。",
                "好");
        }
    }

    private static Page? CurrentPage =>
        Application.Current?.Windows.Count > 0 ? Application.Current.Windows[0].Page : null;

    [ObservableProperty]
    public partial bool CanGoNext { get; set; }

    /// <summary>柱状图的说明文字（"按小时"/"按天"/"按月"）。</summary>
    [ObservableProperty]
    public partial string ChartCaption { get; set; } = string.Empty;

    /// <summary>柱子数据。界面拿到它之后要重画，见 TimePage.xaml.cs。</summary>
    [ObservableProperty]
    public partial IReadOnlyList<BarItem> Bars { get; set; } = Array.Empty<BarItem>();

    /// <summary>四个跨度按钮的高亮状态。XAML 用 DataTrigger 绑它们。</summary>
    public bool IsDay => Span == UsageSpan.Day;
    public bool IsWeek => Span == UsageSpan.Week;
    public bool IsMonth => Span == UsageSpan.Month;
    public bool IsYear => Span == UsageSpan.Year;

    [RelayCommand]
    private void SetSpan(string span)
    {
        if (!Enum.TryParse<UsageSpan>(span, out var parsed) || parsed == Span)
        {
            return;
        }

        Span = parsed;

        // 切换跨度时不改"看的是哪一天"：从"10月27日"切到"周"应该落在本周，
        // 而不是跳回今天。这个行为符合"我现在在看 27 号"的心理模型。
        _ = RefreshAsync(quiet: false);
    }

    [RelayCommand]
    private async Task PrevAsync()
    {
        _anchor = Span switch
        {
            UsageSpan.Day => _anchor.AddDays(-1),
            UsageSpan.Week => _anchor.AddDays(-7),
            UsageSpan.Month => _anchor.AddMonths(-1),
            _ => _anchor.AddYears(-1)
        };

        await RefreshAsync(quiet: false);
    }

    [RelayCommand]
    private async Task NextAsync()
    {
        if (!CanGoNext)
        {
            return;
        }

        _anchor = Span switch
        {
            UsageSpan.Day => _anchor.AddDays(1),
            UsageSpan.Week => _anchor.AddDays(7),
            UsageSpan.Month => _anchor.AddMonths(1),
            _ => _anchor.AddYears(1)
        };

        await RefreshAsync(quiet: false);
    }

    [RelayCommand]
    private void ToggleEntertainment() => IsEntertainmentShown = !IsEntertainmentShown;

    [RelayCommand]
    private async Task LoadAsync() => await RefreshAsync(quiet: false);

    /// <summary>真正干活的那一段。手动刷新走 `LoadAsync`，自动刷新直接调这里。</summary>
    private async Task LoadCoreAsync(bool quiet)
    {
        try
        {
            var (from, to) = Range();
            RangeTitle = BuildTitle(from, to);
            CanGoNext = CanMoveNext();

            // 四个跨度按钮的高亮要跟着变。
            OnPropertyChanged(nameof(IsDay));
            OnPropertyChanged(nameof(IsWeek));
            OnPropertyChanged(nameof(IsMonth));
            OnPropertyChanged(nameof(IsYear));

            var range = await _usage.GetRangeAsync(from, to);

            // 规则表一个应用一行，几十条顶天了。每次刷新读一遍，
            // 换来的是"刚标记完，徽章立刻变"——比加一层缓存再想办法失效简单得多。
            _kindByApp = (await _rules.GetAllAsync())
                .ToDictionary(x => x.AppId, x => x.Kind, StringComparer.OrdinalIgnoreCase);

            TotalText = FormatDuration(range.TotalSeconds);
            IsEmpty = range.TotalSeconds == 0;

            NoteText = range.SessionCount == 0
                ? string.Empty
                : $"记录 {range.SessionCount} 段 · {range.FirstAt:HH:mm} 开始 · {range.LastAt:HH:mm} 结束";

            EntertainmentText = range.TotalSeconds == 0
                ? string.Empty
                : $"娱乐 {FormatDuration(range.EntertainmentSeconds)}"
                  + $"（占 {range.EntertainmentSeconds * 100 / range.TotalSeconds}%）";

            Apps.Clear();
            foreach (var row in BuildAppRows(range, _kindByApp))
            {
                Apps.Add(row);
            }

            HasApps = Apps.Count > 0;

            Bars = Span == UsageSpan.Day
                ? await BuildHourlyBarsAsync(from)
                : BuildDayBars(range, from, to);

            ChartCaption = Span switch
            {
                UsageSpan.Day => "按小时",
                UsageSpan.Week => "按天",
                UsageSpan.Month => "按天",
                _ => "按月"
            };
        }
        catch (Exception ex)
        {
            if (!quiet)
            {
                await ReportErrorAsync(ex);
            }
            else
            {
                // 自动刷新不弹框是对的（每 20 秒弹一个框会把人赶离这一页），
                // 但"吞掉 + 不留痕"意味着失败永远找不到——这次的
                // "数字不动"就是这样查出来的。落一行日志，代价几乎为零。
                Services.CrashLogger.Write("时间页自动刷新失败", ex);
            }

            // 数字暂时停在旧值是可以接受的，下一轮还会再试。
        }
    }

    // ================= 区间与标题 =================

    private (DateOnly From, DateOnly To) Range() => Span switch
    {
        UsageSpan.Day => (_anchor, _anchor.AddDays(1)),
        // 周一为一周之首：中国习惯，而且"这周做了多少"的问题里，
        // 周末属于刚刚过去的那一周，不是新一周的开头。
        UsageSpan.Week => (WeekStart(_anchor), WeekStart(_anchor).AddDays(7)),
        UsageSpan.Month => (new DateOnly(_anchor.Year, _anchor.Month, 1),
                            new DateOnly(_anchor.Year, _anchor.Month, 1).AddMonths(1)),
        _ => (new DateOnly(_anchor.Year, 1, 1), new DateOnly(_anchor.Year + 1, 1, 1))
    };

    private static DateOnly WeekStart(DateOnly day)
    {
        // DayOfWeek 里周日是 0，要先挪成"周一 = 0"再回退。
        var offset = ((int)day.DayOfWeek + 6) % 7;
        return day.AddDays(-offset);
    }

    private string BuildTitle(DateOnly from, DateOnly to)
    {
        var today = DateOnly.FromDateTime(DateTime.Today);

        switch (Span)
        {
            case UsageSpan.Day:
                var name = from == today ? "今天" : WeekdayName(from.DayOfWeek);
                return $"{from.Month}月{from.Day}日 {name}";

            case UsageSpan.Week:
                var end = to.AddDays(-1);
                return $"{from.Month}月{from.Day}日 – {end.Month}月{end.Day}日";

            case UsageSpan.Month:
                return $"{from.Year}年{from.Month}月";

            default:
                return $"{from.Year}年";
        }
    }

    private static string WeekdayName(DayOfWeek day) => day switch
    {
        DayOfWeek.Monday => "周一",
        DayOfWeek.Tuesday => "周二",
        DayOfWeek.Wednesday => "周三",
        DayOfWeek.Thursday => "周四",
        DayOfWeek.Friday => "周五",
        DayOfWeek.Saturday => "周六",
        _ => "周日"
    };

    /// <summary>
    /// 能不能往后翻。
    ///
    /// 【为什么不让翻到未来】
    /// 未来的时间是 0，翻过去只有一片空白和"你今天什么都没做"的错觉。
    /// 一个统计页面不该能制造出这种画面。
    /// </summary>
    private bool CanMoveNext()
    {
        var today = DateOnly.FromDateTime(DateTime.Today);

        return Span switch
        {
            UsageSpan.Day => _anchor < today,
            UsageSpan.Week => WeekStart(_anchor).AddDays(7) <= today,
            UsageSpan.Month => new DateOnly(_anchor.Year, _anchor.Month, 1) < new DateOnly(today.Year, today.Month, 1),
            _ => _anchor.Year < today.Year
        };
    }

    // ================= 图表 =================

    private async Task<IReadOnlyList<BarItem>> BuildHourlyBarsAsync(DateOnly day)
    {
        var hourly = await _usage.GetHourlyAsync(day);

        var bars = new List<BarItem>(24);
        for (var hour = 0; hour < 24; hour++)
        {
            // 24 根柱子如果每根都标数字会糊成一片，每三小时标一个。
            var label = hour % 3 == 0 ? hour.ToString() : string.Empty;
            bars.Add(new BarItem(label, hourly[hour]));
        }

        return bars;
    }

    /// <summary>
    /// 按天/按月的柱子。区间里没有记录的那些天要补 0，
    /// 否则柱子会挤在一起，看着像"这几天连在一起用了很久"。
    /// </summary>
    private IReadOnlyList<BarItem> BuildDayBars(UsageRange range, DateOnly from, DateOnly to)
    {
        var byDay = range.Days.ToDictionary(x => x.Day, x => x.TotalSeconds);

        if (Span == UsageSpan.Year)
        {
            var months = new List<BarItem>(12);
            for (var month = 1; month <= 12; month++)
            {
                var seconds = range.Days
                    .Where(x => x.Day.Month == month)
                    .Sum(x => x.TotalSeconds);

                months.Add(new BarItem($"{month}", seconds));
            }

            return months;
        }

        var bars = new List<BarItem>();
        var isWeek = Span == UsageSpan.Week;

        for (var day = from; day < to; day = day.AddDays(1))
        {
            var seconds = byDay.TryGetValue(day, out var s) ? s : 0;

            // 周视图标周一/周三…，月视图每五天标一个日子。
            var label = isWeek
                ? (day.DayOfWeek == DayOfWeek.Monday ? "一" : string.Empty)
                : (day.Day == 1 || day.Day % 5 == 0 ? day.Day.ToString() : string.Empty);

            bars.Add(new BarItem(label, seconds));
        }

        // 周视图只标了"一"，太孤单，把最后一天补成"日"当刻度终点。
        if (isWeek && bars.Count > 0)
        {
            bars[^1] = bars[^1] with { Label = "日" };
        }

        return bars;
    }

    // ================= 应用排行 =================

    private static IEnumerable<AppUsageRow> BuildAppRows(
        UsageRange range,
        IReadOnlyDictionary<string, AppKind> rules)
    {
        const int TopCount = 8;

        var top = range.Apps.Take(TopCount).ToList();
        if (top.Count == 0)
        {
            yield break;
        }

        // 条形长度按"第一名"归一：这里要表达的是"谁占大头"，
        // 而不是"你用了百分之几的时间"——后者会把所有条都压得很短，看不出差别。
        var max = top[0].Seconds;

        foreach (var app in top)
        {
            yield return new AppUsageRow(
                app.AppId,
                app.DisplayName,
                FormatDuration(app.Seconds),
                max == 0 ? 0 : (double)app.Seconds / max,
                ResolveKind(app.AppId, rules));
        }

        if (range.Apps.Count > TopCount)
        {
            var rest = range.Apps.Skip(TopCount).ToList();
            yield return new AppUsageRow(
                // 这一行是汇总，不对应任何进程，所以 AppId 留空、不能标记。
                string.Empty,
                $"其余 {rest.Count} 个应用",
                FormatDuration(rest.Sum(x => x.Seconds)),
                max == 0 ? 0 : (double)rest.Sum(x => x.Seconds) / max,
                AppKind.Neutral);
        }
    }

    /// <summary>
    /// 这个应用现在算什么。规则表里有的以用户标记为准，否则问内置名单。
    ///
    /// 【为什么判断本身写在 Core 里，这里只做一次查表】
    /// 干预引擎用的是同一个判断（<see cref="EntertainmentApps.Classify"/>）。
    /// 两处各写一遍的话，会出现"时间页标着娱乐、干预却不拦"，
    /// 而且两边都不报错——只能靠人拿两个页面对比才发现。
    /// </summary>
    internal static AppKind ResolveKind(string appId, IReadOnlyDictionary<string, AppKind> rules)
    {
        var marked = rules.TryGetValue(appId, out var kind) ? kind : AppKind.Neutral;
        return EntertainmentApps.Classify(appId, marked);
    }

    // ================= 格式化 =================

    /// <summary>
    /// 把秒数说成人话。
    ///
    /// 【为什么不是「3.2 小时」】
    /// "3.2 小时"要心算才知道是 3 小时 12 分，而人对自己时间的记忆是"三个多小时"。
    /// 分钟级的数字看得见、对得上，也更容易和自己的感受比对。
    /// </summary>
    internal static string FormatDuration(int seconds)    {
        if (seconds <= 0)
        {
            return "0 分钟";
        }

        var totalMinutes = seconds / 60;
        if (totalMinutes < 1)
        {
            return "不到 1 分钟";
        }

        var hours = totalMinutes / 60;
        var minutes = totalMinutes % 60;

        if (hours == 0)
        {
            return $"{minutes} 分钟";
        }

        return minutes == 0 ? $"{hours} 小时" : $"{hours} 小时 {minutes} 分";
    }

    /// <summary>弹一句话给主人看（和错误提示同一套习惯，别一个用 Alert 一个用别的东西）。</summary>
    private static async Task ShowMessageAsync(string title, string message)
    {
        var page = Application.Current?.Windows.Count > 0 ? Application.Current.Windows[0].Page : null;
        if (page is not null)
        {
            await page.DisplayAlertAsync(title, message, "好");
        }
    }

    private static async Task ReportErrorAsync(Exception ex)
    {
        var page = Application.Current?.Windows.Count > 0 ? Application.Current.Windows[0].Page : null;
        if (page is not null)
        {
            await page.DisplayAlertAsync("出错了", ex.Message, "好");
        }
    }
}

/// <summary>
/// 应用排行里的一行。
///
/// 【为什么 Ratio 是按第一名归一，不是按总时长】
/// 按总时长归一的话，前八名加起来如果是总时长的 70%，
/// 所有条都会被压到很短，粗细差别看不出来——那就失去了"扫一眼"的作用。
/// </summary>
/// <param name="AppId">进程名。汇总行（"其余 N 个"）没有对应的进程，为空。</param>
/// <param name="Kind">现在算娱乐、工作还是没标记。徽章和"标记"按钮都看它。</param>
public record AppUsageRow(string AppId, string Name, string DurationText, double Ratio, AppKind Kind)
{
    public string BadgeText => Kind switch
    {
        AppKind.Entertainment => "娱乐",
        AppKind.Work => "工作",
        _ => string.Empty
    };

    public bool HasBadge => Kind != AppKind.Neutral;

    /// <summary>汇总行不能标记，按钮要藏起来——点它没有对象可改。</summary>
    public bool CanMark => !string.IsNullOrEmpty(AppId);
}

/// <summary>柱状图里的一根柱子。</summary>
public record BarItem(string Label, int Seconds);
