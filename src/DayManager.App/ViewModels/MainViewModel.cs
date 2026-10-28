using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using DayManager.App.Messages;
using DayManager.App.Services;
using DayManager.Core.Enums;
using DayManager.Core.Planning;
using DayManager.Data.Repositories;

namespace DayManager.App.ViewModels;

/// <summary>
/// 今日清单页面的大脑。
///
/// 【为什么要有这一层，而不是把逻辑写在 MainPage.xaml.cs 里】
/// 写在界面代码（xaml.cs）里的业务逻辑是没法测试的，而且会和具体控件死死绑在一起。
/// 举个例子：如果"标记完成"写在按钮的 Clicked 事件里，
/// 那么将来想把「左滑删除」「长按标记」改成另一种操作方式，逻辑就得重写一遍。
/// 放在 ViewModel 里，它只是一个叫 ToggleCompleteCommand 的动作，
/// 不管界面用按钮、手势还是快捷键触发，这段逻辑都不用动。
/// </summary>
public partial class MainViewModel : ObservableObject
{
    private readonly ItemRepository _itemRepository;

    /// <summary>
    /// "更多"菜单的实现。
    ///
    /// 【为什么不写在这个类里】
    /// 大任务详情页要对子任务用同一套菜单，写在这就得复制一份。
    /// 抽出去之后两边共用，加一个菜单项只改一处。见 ItemActionService 的说明。
    /// </summary>
    private readonly ItemActionService _actions;

    /// <summary>
    /// 「先做 10 分钟」的计时器。
    ///
    /// 【为什么是可选参数，而不是直接注入】
    /// 它要用系统通知，而通知只有 Windows 有实现——Android 上压根没注册这个服务。
    /// 写成必填的话，那一边一构造这个 ViewModel 就崩。
    /// 可空 + 每个对外属性都判空，是没有这个能力时最省事的安静降级
    /// （时间页的 InterventionService 也是这么做的）。
    /// </summary>
    private readonly FocusSessionService? _focus;

    public MainViewModel(
        ItemRepository itemRepository,
        ItemActionService actions,
        FocusSessionService? focus = null)
    {
        _itemRepository = itemRepository;
        _actions = actions;
        _focus = focus;

        if (_focus is not null)
        {
            // 计时器的状态不在本类里，界面却绑在本类的属性上，
            // 所以"它变了"这件事必须转发过来，否则界面会停在旧状态上。
            // 这里图省事监听了全部变化：计时器一共就六个属性，
            // 每秒一次的通知量也不值得再细分。
            _focus.PropertyChanged += (_, _) =>
            {
                OnPropertyChanged(nameof(FocusActive));
                OnPropertyChanged(nameof(FocusRunning));
                OnPropertyChanged(nameof(FocusFinished));
                OnPropertyChanged(nameof(FocusRunningText));
                OnPropertyChanged(nameof(FocusPromptText));
            };
        }

        //
        // 订阅"日程变了"这个消息：拆解页写入完成之后会发一条过来。
        //
        // 【为什么要订阅，光靠 OnAppearing 不够吗】
        // 不够。MAUI 的 Shell 在底部 Tab 之间切换时，ContentPage 的 OnAppearing
        // 并不保证触发，结果就是：拆解页明明写了 10 条任务，切回「今天」还是空的。
        // 这条消息把"什么时候该刷新"的决定权交给改数据的那一方——
        // 谁改的谁知道要刷，比让读的人去猜可靠。
        //
        // 【为什么在构造函数里注册，而不是页面 OnAppearing 里】
        // 消息可能在页面还没显示时就到了（拆解页就在隔壁 Tab），
        // 注册晚了这条消息就丢了。构造时就挂上，任何时候到达都能收到。
        //
        WeakReferenceMessenger.Default.Register<ItemsChangedMessage>(
            this,
            (_, _) => _ = LoadCommand.ExecuteAsync(null));
    }

    /// <summary>
    /// 界面上显示的那串列表。
    ///
    /// 【为什么用 ObservableCollection 而不是 List】
    /// ObservableCollection 会在内容增删时自动通知界面刷新（底层实现了 INotifyCollectionChanged）。
    /// 换成 List 的话，调用 Add/Remove 之后界面纹丝不动，必须手动告诉控件"数据变了"。
    /// 这是新手最容易卡住的一处：数据明明加了，界面却没反应。
    /// </summary>
    public ObservableCollection<ItemViewModel> TodayItems { get; } = new();

    // ================= 当前查看的那一天 =================

    /// <summary>
    /// 当前正在看哪一天。
    ///
    /// 【为什么要能翻到其他日子，而不是只给"今天"】
    /// 只显示今天有两个实际后果：
    ///   1. 提前往某天放一件事后，在那一天到来之前根本找不到它，
    ///      等于"排好的计划在系统里是隐形的"；
    ///   2. 想回头看看昨晚做完什么，也没有地方可看——
    ///      而这个软件的正反馈恰恰建立在"看见自己做到过"这件事上。
    /// 所以翻日期不是锦上添花，是让"计划"这个词真正成立的前提。
    ///
    /// 【为什么默认是今天，而不是"上次看到的那天"】
    /// 记着上次的日子会导致某天打开软件看到的是三天前，
    /// 第一反应是"我的今天怎么空的"。这个软件的入口必须是"此刻"。
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DayLabelText))]
    [NotifyPropertyChangedFor(nameof(RelativeDayText))]
    [NotifyPropertyChangedFor(nameof(IsViewingToday))]
    [NotifyPropertyChangedFor(nameof(IsNotViewingToday))]
    [NotifyPropertyChangedFor(nameof(EmptyHint))]
    [NotifyPropertyChangedFor(nameof(AddPlaceholder))]
    public partial DateTime SelectedDate { get; set; } = DateTime.Today;

    /// <summary>日期选择器能用上的最早日期（防止误点到 1900 年那种无意义的位置）。</summary>
    public DateTime EarliestDate => new(2020, 1, 1);

    private DateOnly SelectedDay => DateOnly.FromDateTime(SelectedDate);

    /// <summary>中间那行大字："10月26日 星期一"。</summary>
    public string DayLabelText => SelectedDay.ToString("M月d日 dddd", new CultureInfo("zh-CN"));

    /// <summary>
    /// 紧跟着的相对说法：今天 / 明天 / 昨天 / 3 天后。
    ///
    /// 【为什么非要写这一句】
    /// 用户看日期时要自己在心里换算"11月2日离今天还有几天"，
    /// 多一次心算就多一点摩擦。而且这种换算很容易出错，
    /// 出错了就变成"我以为还有很久"——正是要防治的错觉。
    /// </summary>
    public string RelativeDayText
    {
        get
        {
            var offset = SelectedDay.DayNumber - DateOnly.FromDateTime(DateTime.Today).DayNumber;
            return offset switch
            {
                0 => "今天",
                1 => "明天",
                2 => "后天",
                -1 => "昨天",
                > 0 => $"{offset} 天后",
                _ => $"{-offset} 天前"
            };
        }
    }

    public bool IsViewingToday => SelectedDay == DateOnly.FromDateTime(DateTime.Today);

    public bool IsNotViewingToday => !IsViewingToday;

    /// <summary>列表为空时说的话，在看别的日子时会跟着变。</summary>
    public string EmptyHint => IsViewingToday
        ? "今天还没有安排。&#10;在下面加一条，哪怕只是「先看两分钟」。"
        : "这一天还没有安排。&#10;可以提前把想做的事放进来，那天就自动出现。";

    /// <summary>
    /// 底部输入框的提示语也跟着日期变。
    /// 在翻到后天的时候看到"想做点什么？"会让人犹豫（是加到今天还是后天？），
    /// 写清楚"加到 10月28日"就不用猜。
    /// </summary>
    public string AddPlaceholder => IsViewingToday
        ? "想做点什么？"
        : $"加到 {SelectedDay:M月d日}……";

    [ObservableProperty]
    public partial string SummaryText { get; set; } = "今天还没有安排";

    [ObservableProperty]
    public partial double CompletionRatio { get; set; }

    [ObservableProperty]
    public partial string NewTitle { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool UseSpecificTime { get; set; }

    [ObservableProperty]
    public partial TimeSpan NewTime { get; set; } = new(9, 0, 0);

    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    /// <summary>加载过程中又来了一次加载请求（见 LoadAsync 里的说明）。</summary>
    private bool _reloadPending;

    // ================= 命令 =================

    /// <summary>
    /// [RelayCommand] 会自动生成一个叫 LoadCommand 的属性（去掉方法名结尾的 Async）。
    /// XAML 里就写 Command="{Binding LoadCommand}"。
    /// 手写这个命令本来需要定义 ICommand 字段、写 CanExecute、还要处理同步异步，二三十行起步。
    /// </summary>
    [RelayCommand]
    private async Task LoadAsync()
    {
        //
        // 【忙的时候为什么不排队，而是记一下"待会儿再来一遍"】
        // 直接 return 会丢掉这次请求：用户连点了两下翻页，
        // 第二次很可能是在第一次加载还没结束时发生的，
        // 被丢掉之后界面显示的就是上一个日期的内容——日期变了、清单没变，
        // 用户会以为数据错了。
        // 记一个标记，等手头这轮跑完再补一次，最后一次的结果总能落到界面上。
        //
        if (IsBusy)
        {
            _reloadPending = true;
            return;
        }

        IsBusy = true;
        try
        {
            do
            {
                _reloadPending = false;
                await LoadForSelectedDayAsync();
            }
            while (_reloadPending);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>按当前选中的那一天读数据。</summary>
    private async Task LoadForSelectedDayAsync()
    {
        try
        {
            var items = await _itemRepository.GetDayListAsync(SelectedDay);

            TodayItems.Clear();
            foreach (var item in items)
            {
                TodayItems.Add(new ItemViewModel(item));
            }

            UpdateSummary();
        }
        catch (Exception ex)
        {
            await ReportErrorAsync(ex);
        }
    }

    // ================= 翻页 =================

    [RelayCommand]
    private async Task PreviousDayAsync()
    {
        SelectedDate = SelectedDate.AddDays(-1);
        await LoadAsync();
    }

    [RelayCommand]
    private async Task NextDayAsync()
    {
        SelectedDate = SelectedDate.AddDays(1);
        await LoadAsync();
    }

    /// <summary>一步回到今天。</summary>
    [RelayCommand]
    private async Task GoTodayAsync()
    {
        SelectedDate = DateTime.Today;
        await LoadAsync();
    }

    [RelayCommand]
    private async Task AddItemAsync()
    {
        // 用户可能在没输入的情况下点了按钮，这里静默忽略即可，
        // 弹窗骂人只会增加使用摩擦。
        if (string.IsNullOrWhiteSpace(NewTitle))
        {
            return;
        }

        try
        {
            // 【在别的日子里加东西时，时间必须挂到那一天】
            // 如果不管翻到哪天都按今天记，翻页就变成"只能看、不能写"，
            // 而且用户完全察觉不到记错了——因为他写完之后，这条当场显示在看着的那天。
            // 等真正到了那一天才发现它不见了，那时已经很难回溯当时发生了什么。
            await _itemRepository.AddToDayAsync(
                NewTitle.Trim(),
                SelectedDay,
                UseSpecificTime ? NewTime : null);

            // 清空输入框并关掉时间开关，让用户可以直接接着输入下一条。
            // 这种"添加后可以连续录入"的手感，决定了你会不会真的每天用它记录。
            NewTitle = string.Empty;
            UseSpecificTime = false;

            await LoadAsync();
        }
        catch (Exception ex)
        {
            await ReportErrorAsync(ex);
        }
    }

    /// <summary>
    /// 打勾 / 取消打勾。
    ///
    /// 【为什么这里只做完成和撤销，没有"进行中"】
    /// 打勾框的心智模型是二元的：做了 / 没做。
    /// "进行中"是一个需要额外决策的中间态，放在每次都要点的按钮上会让人犹豫，
    /// 所以它放在右侧的 ⋯ 菜单里。这是刻意区分常用操作和不常用操作。
    /// </summary>
    [RelayCommand]
    private async Task ToggleCompleteAsync(ItemViewModel item)
    {
        try
        {
            var updated = await _itemRepository.SetStatusAsync(
                item.Id,
                item.IsDone ? ItemStatus.Pending : ItemStatus.Done);

            if (updated is not null)
            {
                item.Update(updated);
                UpdateSummary();
            }
        }
        catch (Exception ex)
        {
            await ReportErrorAsync(ex);
        }
    }

    /// <summary>
    /// 右侧「更多」菜单：重命名 / 删除 / 改状态。
    ///
    /// 【为什么实现不在本类里】
    /// 大任务详情页的子任务要用同一套菜单，写在这就得整段复制。
    /// 现在两边共用 ItemActionService，加一个菜单项只改一处。
    /// 传 LoadAsync 进去是因为改完之后要按当前日期重查——
    /// 服务不知道这一页是按天组织的，刷新的方式只能由调用方给。
    /// </summary>
    [RelayCommand]
    private async Task ShowActionsAsync(ItemViewModel item)
    {
        await _actions.ShowMenuAsync(item, LoadAsync);
    }

    // ================= 「先做 10 分钟」 =================

    public bool FocusActive => _focus?.IsActive == true;

    /// <summary>
    /// 正在计时（还没到点）。
    ///
    /// 【为什么"在计时"和"到点了"要分成两个状态】
    /// 界面上这是两块完全不同的东西：一块是倒计时，一块是收尾时的正反馈和选择。
    /// 合成一个的话，XAML 里就要靠一层层 IsVisible 去区分，改一处容易漏另一处。
    /// </summary>
    public bool FocusRunning => _focus is { IsActive: true, IsFinished: false };

    public bool FocusFinished => _focus?.IsFinished == true;

    /// <summary>计时中那一行文字："正在做：写报告 · 剩 9:58"。</summary>
    public string FocusRunningText => _focus is null
        ? string.Empty
        : $"正在做：{_focus.ItemTitle} · 剩 {_focus.RemainingText}";

    public string FocusPromptText => _focus?.PromptText ?? string.Empty;

    /// <summary>
    /// 点条目上的「10 分钟」。
    ///
    /// 【为什么点正在计时的那一条是"结束"而不是"再来一轮"】
    /// 按钮上写着「10 分钟」，再点一次合理的期待是"让它停下"。
    /// 如果它变成"又来一轮"，按钮的含义就随状态漂移了——
    /// 每次点之前都得先想一下现在点下去是什么。含义固定的按钮才不用想。
    /// </summary>
    [RelayCommand]
    private async Task StartFocusAsync(ItemViewModel item)
    {
        if (_focus is null)
        {
            return;
        }

        try
        {
            if (_focus.IsActive && _focus.ItemId == item.Id)
            {
                _focus.FinishNow();
                return;
            }

            var updated = await _focus.StartAsync(item.Id, item.Title);

            if (updated is not null)
            {
                // 刷新这一行：状态刚被改成"进行中"，徽章和副标题都要跟着变。
                item.Update(updated);
                UpdateSummary();
            }
        }
        catch (Exception ex)
        {
            await ReportErrorAsync(ex);
        }
    }

    [RelayCommand]
    private async Task ContinueFocusAsync()
    {
        if (_focus is null)
        {
            return;
        }

        try
        {
            await _focus.ContinueAsync();
        }
        catch (Exception ex)
        {
            await ReportErrorAsync(ex);
        }
    }

    /// <summary>「今天先到这」/ 计时中的「停」。</summary>
    [RelayCommand]
    private void EndFocus()
    {
        _focus?.End();
    }

    private async Task ChangeStatusAsync(ItemViewModel item, ItemStatus status)
    {
        var updated = await _itemRepository.SetStatusAsync(item.Id, status);
        if (updated is not null)
        {
            item.Update(updated);
            UpdateSummary();
        }
    }

    // ================= 辅助 =================

    /// <summary>
    /// 刷新顶部的完成统计。
    ///
    /// 【为什么完成数不用查数据库】
    /// 列表已经在内存里了，直接数一遍比再查一次库更快也更简单。
    /// 只有"虚假的优化"才会坚持用 SQL 的 COUNT 去取一个明明就在眼前的数字。
    /// </summary>
    private void UpdateSummary()
    {
        var total = TodayItems.Count;
        var done = TodayItems.Count(x => x.IsDone);

        // 除零保护：清单为空时不能让进度条炸掉，也不能显示 NaN%
        CompletionRatio = total == 0 ? 0 : (double)done / total;

        SummaryText = total == 0
            ? (IsViewingToday ? "今天还没有安排" : "这一天还没有安排")
            : $"已完成 {done} / {total} 件"
              // 把"这一天总共要花多久"一并说出来：这是决定"今天能不能开始"的第一信息，
              // 而它原本要用户自己在心里把每行的估时加起来。
              + TotalEstimateText();
    }

    /// <summary>清单里所有任务估时的合计。没有估过时的任务不参与，所以可能为空串。</summary>
    private string TotalEstimateText()
    {
        var minutes = TodayItems.Sum(x => x.Item.EstimateMinutes ?? 0);
        return minutes > 0 ? $"，合计 {DurationText.Format(minutes)}" : string.Empty;
    }

    /// <summary>
    /// 当前显示的页面。弹窗类操作必须从页面对象上发起。
    ///
    /// 【为什么不直接用 Application.Current.MainPage】
    /// 那是早期 Xamarin.Forms 的写法，MAUI 推荐从 Windows 集合取，
    /// 因为现在支持多窗口（同一个 App 可以同时开好几个窗口）。
    /// </summary>
    private static Page? CurrentPage =>
        Application.Current?.Windows.Count > 0 ? Application.Current.Windows[0].Page : null;

    /// <summary>
    /// 统一处理异常。
    ///
    /// 【为什么每个命令都要 try-catch】
    /// 数据库操作在真机上随时可能失败：文件被占用、磁盘满、权限被系统回收……
    /// 不捕获的话就是直接崩溃，用户只看到一个闪退，
    /// 你连哪里出错都不知道。显示出来至少能立刻知道原因。
    /// </summary>
    private static async Task ReportErrorAsync(Exception ex)
    {
        var page = CurrentPage;
        if (page is not null)
        {
            await page.DisplayAlertAsync("出错了", ex.Message, "好");
        }
    }
}
