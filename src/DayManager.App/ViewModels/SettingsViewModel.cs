using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DayManager.App.Drawables;
using DayManager.App.Services;
using DayManager.Core.Ai;
using DayManager.Core.Enums;
using DayManager.Data.Repositories;

namespace DayManager.App.ViewModels;

/// <summary>
/// 设置页的大脑：所有"填一次就再也不看"的东西。
///
/// 【为什么单独开一页，而不是继续堆在时间页】
/// 时间页原本装着统计（本职）+ 五张设置卡，越堆越长，
/// 而其中四张（外观、AI 说话方式、AI 接入、后台与开机）跟"时间去了哪"毫无关系。
/// 时间页是每天要翻的，设置是一年不点几次的，混在一起的结果是
/// 每次想看今天的时长都要先滚过一堆偏好设置。
///
/// 【为什么这一页不占 Tab】
/// Tab 的顺序是优先级的表达（对话放第一）。设置不是工作流目的地，
/// 是"偶尔去一次"的地方——占一个 Tab 等于天天给它留着一块没人看的位子。
/// 所以和「拆解」一样注册成普通页面，从时间页底部一行入口进来，
/// 系统的返回箭头和底部 Tab 都还在。
/// </summary>
public partial class SettingsViewModel : ObservableObject
{
    private readonly PolicyRepository _policy;
    private readonly AiSettings _ai;
    private readonly ApiClient? _api;
    private readonly AvatarService _avatars;

    /// <summary>
    /// 托盘与开机自启。可空：这是 Windows 特有的能力，
    /// Android 上没有托盘这回事，注入 null 时这一块就不显示。
    /// </summary>
    private readonly IWindowShell? _shell;
    private readonly IAutoStartService? _autoStart;

    private readonly ThemeService _theme;

    public SettingsViewModel(
        PolicyRepository policy,
        AiSettings ai,
        AvatarService avatars,
        ApiClient? api = null,
        IWindowShell? shell = null,
        IAutoStartService? autoStart = null,
        ThemeService? theme = null)
    {
        AiAvatar = new AvatarDrawable(avatars.Ai);
        MyAvatar = new AvatarDrawable(avatars.Me);
        _policy = policy;
        _ai = ai;
        _api = api;
        _avatars = avatars;
        _shell = shell;
        _autoStart = autoStart;
        _theme = theme ?? new ThemeService(policy);
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

    /// <summary>联网搜索的密钥。留空是正常用法：AI 只是不能上网查，不会因此报错。</summary>
    [ObservableProperty]
    public partial string SearchKeyInput { get; set; } = string.Empty;

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
        _ai.SearchApiKey = (SearchKeyInput ?? string.Empty).Trim();
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

    /// <summary>
    /// 他自己写的那段人设。可空；能写多行，最多 <see cref="AiTone.MaxPersonaLength"/> 字。
    /// </summary>
    [ObservableProperty]
    public partial string AiPersona { get; set; } = string.Empty;

    /// <summary>字数提示。写在输入框右下角，让人知道还能写多少。</summary>
    public string PersonaCountText =>
        $"{AiTone.NormalizePersona(AiPersona).Length} / {AiTone.MaxPersonaLength} 字";

    /// <summary>
    /// 停手一会儿就自己存。
    ///
    /// 【为什么改成"停手就存"而不是只靠失焦】
    /// 失焦存对人设框来说不再够用：现在能写几百字，
    /// 写到一半被别的窗口抢走焦点、或者压根忘了点别处直接关掉页面，
    /// 丢的就不是"最后几个字"而是一整段设定。
    /// 延迟排队的写法能兼顾两头——连续打字只在最后一次停手时写一次库，
    /// 不会像每敲一个字存一次那样把一行设置变成几十行垃圾。
    /// </summary>
    private CancellationTokenSource? _personaSaveCts;

    partial void OnAiPersonaChanged(string value)
    {
        OnPropertyChanged(nameof(PersonaCountText));
        QueuePersonaSave();
    }

    private void QueuePersonaSave()
    {
        _personaSaveCts?.Cancel();
        _personaSaveCts?.Dispose();

        var cts = new CancellationTokenSource();
        _personaSaveCts = cts;

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(1200, cts.Token);
                await SavePolicyAsync(PolicyKeys.AiPersona, AiTone.NormalizePersona(AiPersona));
            }
            catch (OperationCanceledException)
            {
                // 又接着打了：这一次取消，新的那次会负责存。
            }
        });
    }

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
    /// 存这段人设。失焦时也会走一次（<see cref="QueuePersonaSave"/> 负责打字中途的自动存）。
    ///
    /// 【为什么不丢那个自动存的时机】
    /// 两边都存同一个地方、同一个值，重复写一次的成本远小于"他以为存了其实没存"。
    /// </summary>
    [RelayCommand]
    private async Task SavePersonaAsync()
    {
        // 已经有自动存在排队了，先把它撤掉，免得紧接着再写一遍同样的内容。
        _personaSaveCts?.Cancel();
        await SavePolicyAsync(PolicyKeys.AiPersona, AiTone.NormalizePersona(AiPersona));
    }

    // ==================== 两边头像 ====================

    /// <summary>
    /// AI 那支笔。界面上所有 AI 头像圆共用同一个实例：
    /// 它不是每个圆一份的状态，只是"知道现在该画哪张图"的同一个对象。
    /// </summary>
    public AvatarDrawable AiAvatar { get; }

    /// <summary>你自己那支笔。同上。</summary>
    public AvatarDrawable MyAvatar { get; }

    /// <summary>
    /// 已经换过自定义头像（决定「恢复默认」那个按钮要不要露出来）。
    ///
    /// 【为什么按"图还在不在"算，而不是按"存没存过"】
    /// 头像文件在本机应用数据目录里，他有可能自己翻进去删掉。
    /// 那种情况下界面应该回到没换过的样子，而不是显示一个永远画不出来的设置。
    /// </summary>
    [ObservableProperty]
    public partial bool HasCustomAiAvatar { get; set; }

    /// <summary>同上，你自己那一侧。</summary>
    [ObservableProperty]
    public partial bool HasCustomMyAvatar { get; set; }

    /// <summary>
    /// 给 AI 换一张。
    ///
    /// 【为什么失败要说一句，而不是静默】
    /// 选图失败几乎都发生在他确实选了东西之后（格式怪、读不出来），
    /// 静默的表现是"头像没变"，他会以为自己点错了。
    /// </summary>
    [RelayCommand]
    private async Task PickAiAvatarAsync()
    {
        var ok = await _avatars.Ai.PickAsync();
        HasCustomAiAvatar = _avatars.Ai.HasCustom;

        if (!ok)
        {
            await ShowMessageAsync("没换成", "没选到图片，或者这张图读不出来。换一张（png / jpg）试试。");
        }
    }

    /// <summary>AI 恢复成默认的那个格子。</summary>
    [RelayCommand]
    private void ClearAiAvatar()
    {
        _avatars.Ai.Clear();
        HasCustomAiAvatar = _avatars.Ai.HasCustom;
    }

    /// <summary>给自己换一张。</summary>
    [RelayCommand]
    private async Task PickMyAvatarAsync()
    {
        var ok = await _avatars.Me.PickAsync();
        HasCustomMyAvatar = _avatars.Me.HasCustom;

        if (!ok)
        {
            await ShowMessageAsync("没换成", "没选到图片，或者这张图读不出来。换一张（png / jpg）试试。");
        }
    }

    /// <summary>自己那侧恢复成默认的人影。</summary>
    [RelayCommand]
    private void ClearMyAvatar()
    {
        _avatars.Me.Clear();
        HasCustomMyAvatar = _avatars.Me.HasCustom;
    }

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

    // ================= 后台与开机（托盘） =================

    /// <summary>
    /// 有没有"后台与开机"这一块。
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

    // ==================== 读库 / 存库 ====================

    /// <summary>插值一次就够：这一页只在打开时读一次库。</summary>
    private bool _loaded;

    /// <summary>
    /// 进页面时把所有设置读一次。
    ///
    /// 【为什么每次进来都重读，而不是只第一次】
    /// 密钥可能在别处改过（比如别人在这台机器上填过），
    /// 而这一页是 Transient，每次进来都是新的实例——重读一次的成本比"显示旧值"小得多。
    /// </summary>
    [RelayCommand]
    private async Task LoadAsync()
    {
        if (_loaded)
        {
            return;
        }

        _loaded = true;

        try
        {
            var closeToTray = await _policy.GetBoolAsync(PolicyKeys.CloseToTray, true);
            var tone = AiTone.Parse(await _policy.GetRawAsync(PolicyKeys.AiTone));
            var persona = await _policy.GetRawAsync(PolicyKeys.AiPersona);

            SelectThemeOnly(_theme.Mode);
            SelectToneOnly(tone);
            AiPersona = AiTone.NormalizePersona(persona);
            OnPropertyChanged(nameof(PersonaCountText));
            HasCustomAiAvatar = _avatars.Ai.HasCustom;
            HasCustomMyAvatar = _avatars.Me.HasCustom;

            // 刚读出来就被排了一次自动存，那是把已经存好的东西再写一遍。撤掉。
            _personaSaveCts?.Cancel();
            IsCloseToTray = closeToTray;

            // 托盘那边马上要知道：它在任何界面操作之前就得按这个偏好拦住"关闭"。
            _shell?.SetCloseToTray(IsCloseToTray);

            // 密钥属于"连接设置"，不进 policy_settings：那张表在数据库里，
            // 而数据库是要能被拷给别人看数据的——密钥绝不能搭这趟车。
            AiKeyInput = _ai.ApiKey;
            AiBaseUrlInput = _ai.BaseUrl;
            AiModelInput = _ai.Model;
            SearchKeyInput = _ai.SearchApiKey;
            AiStatusOk = _ai.HasKey;
            AiStatusText = _ai.HasKey
                ? $"已保存（{AiSettings.Mask(_ai.ApiKey)}）。回对话页说句话就是它在回答。"
                : "还没填密钥，AI 现在没法回答。";

            if (_autoStart is not null)
            {
                IsAutoStartSupported = _autoStart.IsSupported;
                IsAutoStartOn = _autoStart.IsEnabled;
            }
        }
        catch (Exception ex)
        {
            CrashLogger.Write("读取设置", ex);
        }
    }

    /// <summary>
    /// 返回上一页。
    ///
    /// 【为什么自己做返回按钮而不是用系统导航栏】
    /// 大任务详情页踩过一次：系统那个箭头又小又浅，
    /// 而"进了设置出不来"是最让人怀疑自己点错的一类体验。
    /// 详见 ProjectDetailPage.xaml 顶部的说明。
    /// </summary>
    [RelayCommand]
    private async Task BackAsync() => await Shell.Current.GoToAsync("..");

    private async Task SavePolicyAsync(string key, string value)
    {
        try
        {
            // Source 写 User：需求 2.7 定了用户手动改过的参数 AI 不得再自动覆盖。
            await _policy.SetAsync(key, value, PolicySource.User);
        }
        catch (Exception ex)
        {
            CrashLogger.Write("保存设置", ex);
        }
    }

    private static async Task ShowMessageAsync(string title, string message)
    {
        var page = Application.Current?.Windows.Count > 0 ? Application.Current.Windows[0].Page : null;
        if (page is not null)
        {
            await page.DisplayAlertAsync(title, message, "好");
        }
    }
}
