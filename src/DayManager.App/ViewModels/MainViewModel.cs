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
/// 底部输入框「这一条要建成什么形态」。
///
/// 【为什么是界面层自己的枚举，不复用 Core 的 ItemShape】
/// ItemShape 是落库之后的数据契约，描述一条记录**是什么**；
/// 这里描述的是用户此刻**想建哪一种**，只活到「加入」按钮按下去为止。
/// 两者刻意不同名：万一以后把「容器 / 不判成败」也开放给用户，
/// 界面这边加一个取值不会影响数据库那三根轴。
/// 取值没有容器和「不判成败」——那两样还没定案，本轮明确不给入口。
/// </summary>
public enum AddShape
{
    Day = 0,        // 今天做（默认）：定日待办
    Timed = 1,      // 定点：带开始时间
    Deadline = 2,   // 只有截止日：常驻带
    Daily = 3       // 每天：每次各算（RRule 展开仍留白，只建这一条）
}

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

        // 换主题后要重读：每一行的标题色、徽章色是 Refresh 那一刻算出来的，
        // 不重读它们就停在旧主题的值上，界面变成深浅混搭。
        WeakReferenceMessenger.Default.Register<ThemeChangedMessage>(
            this,
            (_, _) => _ = LoadCommand.ExecuteAsync(null));

        StartElapsedTicker();
    }

    /// <summary>
    /// 每 20 秒把所有正在走表的条目的"已进行 N 分钟"刷一遍。
    ///
    /// 【为什么非要有这个节拍】
    /// 一个不会自己走的数字不是计时，只是快照。
    /// 它存在的意义是让"已经投入了多少"随时可见——要人自己去点一下
    /// 才更新，那就等于没有。
    ///
    /// 【为什么是 20 秒而不是每秒】
    /// 这个数字人的眼睛读到的是"二十几分钟"这种量级，
    /// 每秒刷要平白重排六十倍的布局；二十秒最慢也就差二十秒，看不出来。
    ///
    /// 【为什么只看 IsCounting 的那几条】
    /// 暂停中的、没开始的，它们的值不会自己变，重算是白算。
    ///
    /// 【为什么用 Application 的 Dispatcher 起，而不是 new Timer】
    /// Dispatcher.StartTimer 是在 UI 线程上调回来的，
    /// 改那些被绑定的属性是安全的；普通 Timer 的回调在线程池里，
    /// 直接改 ObservableCollection 绑定的属性会炸。缺点是程序一关它就停了，
    /// 而这正好是我们要的——它本来只服务于屏幕上的那个数字。
    /// </summary>
    private void StartElapsedTicker()
    {
        Application.Current?.Dispatcher.StartTimer(
            TimeSpan.FromSeconds(20),
            () =>
            {
                // 两个集合都要刷：常驻项点了「10 分钟」也会走进行中、走表，
                // 只刷 TodayItems 的话，常驻带上那个「已进行 N 分钟」会一直停在旧数字上。
                foreach (var item in TodayItems.Concat(FloatingItems).Where(x => x.IsCounting).ToList())
                {
                    item.RefreshElapsed();
                }

                // 返回 true = 还要下一次；返回 false 这个定时器就结束了。
                return true;
            });
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

    /// <summary>
    /// 「一直悬着、还没到截止日的事」—— 独立一带，不并进 TodayItems。
    ///
    /// 【为什么必须是两个集合，而不是往 TodayItems 里塞一种特殊行】
    /// 完成率的分母就是 TodayItems.Count（见 UpdateSummary）。
    /// 常驻项一旦混进去，就会天天被算成「今天又没做完的一件」，
    /// 正好是 5.2 里最要避免的那一串欠账。分开装，分母一行都不用改。
    /// 它们也不滚动在主清单里，是靠 BindableLayout 列在清单上方的一小带。
    /// </summary>
    public ObservableCollection<ItemViewModel> FloatingItems { get; } = new();

    /// <summary>
    /// 常驻带只在看「今天」时出现。翻到别的日子看的是那天发生过/该做什么，
    /// 不该混进「现在还悬着的事」——那是相对此刻说的，不是相对那一天。
    /// </summary>
    public bool ShowFloatingBand => IsViewingToday && FloatingItems.Count > 0;

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
    [NotifyPropertyChangedFor(nameof(ShowFloatingBand))]
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
    /// 底部输入框的提示语跟着「翻到哪天」和「选了哪种形态」一起变。
    /// 在翻到后天的时候看到"想做点什么？"会让人犹豫（是加到今天还是后天？），
    /// 写清楚"加到 10月28日"就不用猜；选了截止日/每天之后，
    /// 提示语再说一遍当前形态，是防「我以为在建普通待办」的最后一道保险。
    /// </summary>
    public string AddPlaceholder => NewItemShape switch
    {
        AddShape.Timed => $"加到 {SelectedDay:M月d日} 几点？",
        AddShape.Deadline => $"什么事，{DeadlineDate:M月d日} 前？",
        AddShape.Daily => "每天要做的事？",
        _ => IsViewingToday ? "想做点什么？" : $"加到 {SelectedDay:M月d日}……"
    };

    [ObservableProperty]
    public partial string SummaryText { get; set; } = "今天还没有安排";

    [ObservableProperty]
    public partial double CompletionRatio { get; set; }

    [ObservableProperty]
    public partial string NewTitle { get; set; } = string.Empty;

    /// <summary>
    /// 这一条要建成哪种形态。默认「今天做」——最高频的动作不该多点一下。
    /// 每次「加入」之后会重置回这里，免得连着录第二条时还停在上一个形态。
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShapeButtonText))]
    [NotifyPropertyChangedFor(nameof(ShowTimePicker))]
    [NotifyPropertyChangedFor(nameof(ShowDatePicker))]
    [NotifyPropertyChangedFor(nameof(AddPlaceholder))]
    public partial AddShape NewItemShape { get; set; } = AddShape.Day;

    /// <summary>形态选择区是否展开。收起时第 5 行塌成 0 高，不占清单空间。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowTimePicker))]
    [NotifyPropertyChangedFor(nameof(ShowDatePicker))]
    public partial bool IsShapePickerOpen { get; set; }

    [ObservableProperty]
    public partial TimeSpan NewTime { get; set; } = new(9, 0, 0);

    /// <summary>「只有截止日」形态下挑的截止日，默认一周后。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AddPlaceholder))]
    public partial DateTime DeadlineDate { get; set; } = DateTime.Today.AddDays(7);

    /// <summary>截止日不许挑过去：新建出来的东西不该一落地就是欠账。</summary>
    public DateTime DeadlineMinDate => DateTime.Today;

    /// <summary>
    /// 形态按钮上那几个字。后缀用小写的 v 当下拉提示，是**故意不用 ▾ / ‹ ›**：
    /// 11-02 真事，界面写了系统字体里没有的字形，两个按钮全空，编译 0 错 0 警。
    /// ASCII 的 v 每个字体都有，长得也够像箭头。
    /// </summary>
    public string ShapeButtonText => NewItemShape switch
    {
        AddShape.Timed => "定点 v",
        AddShape.Deadline => "截止日 v",
        AddShape.Daily => "每天 v",
        _ => "今天 v"
    };

    /// <summary>只有「定点」且形态选择区收起时才显示时间选择器。</summary>
    public bool ShowTimePicker => !IsShapePickerOpen && NewItemShape == AddShape.Timed;

    /// <summary>只有「只有截止日」且形态选择区收起时才显示日期选择器。</summary>
    public bool ShowDatePicker => !IsShapePickerOpen && NewItemShape == AddShape.Deadline;

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

            // 常驻带锚定「真实今天」，不跟 SelectedDay 走：它表达的是
            // 「从现在到截止日这一整段」，跟用户翻到哪一天无关。
            // 翻页时 ShowFloatingBand 会因为 IsViewingToday 变假而收起这一带。
            var today = DateOnly.FromDateTime(DateTime.Today);
            var floating = await _itemRepository.GetFloatingDeadlinesAsync(today);

            FloatingItems.Clear();
            foreach (var item in floating)
            {
                FloatingItems.Add(new ItemViewModel(item, isFloating: true));
            }

            UpdateSummary();
            // FloatingItems 是 ObservableCollection，CollectionChanged 不会自动
            // 通知这个计算属性，改完内容得手动喊一声，否则翻回今天这一带不出现。
            OnPropertyChanged(nameof(ShowFloatingBand));
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

    /// <summary>点最左边的形态按钮：展开 / 收起那一排选项。</summary>
    [RelayCommand]
    private void ToggleShapePicker()
    {
        IsShapePickerOpen = !IsShapePickerOpen;
    }

    /// <summary>
    /// 选一种形态。CommandParameter 传字符串而不是枚举，是因为 XAML 里写
    /// "Timed" 比写 x:Static 简单，也不会拼错类型全名；认不出来的值一律退回「今天」。
    /// </summary>
    [RelayCommand]
    private void PickShape(string shape)
    {
        NewItemShape = shape switch
        {
            nameof(AddShape.Timed) => AddShape.Timed,
            nameof(AddShape.Deadline) => AddShape.Deadline,
            nameof(AddShape.Daily) => AddShape.Daily,
            _ => AddShape.Day
        };
        IsShapePickerOpen = false;
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
            var title = NewTitle.Trim();

            // 【四种形态各走一个专用创建入口，别靠时间字段反推】
            // 11-21 那次事故的根，就是 date 和 dueDate 落库后长得一模一样、
            // 下游只能猜。现在 Shape 由入口写死，界面这边只负责选入口。
            // 定点 / 定日仍挂在 SelectedDay 上：翻到哪天写的就记到哪天，
            // 否则翻页会变成"只能看、不能写"，而且当场显示正确、隔天消失，最难回溯。
            switch (NewItemShape)
            {
                case AddShape.Timed:
                    await _itemRepository.AddToDayAsync(title, SelectedDay, NewTime);
                    break;
                case AddShape.Deadline:
                    // 截止日跟当前翻到哪天无关：常驻带锚定真实今天，不随 SelectedDay 走。
                    await _itemRepository.AddDeadlineAsync(
                        title, DateOnly.FromDateTime(DeadlineDate));
                    break;
                case AddShape.Daily:
                    // 每次各算一次。RRule 真实展开还留白，这里只建这一天的一条，
                    // 别为了"看起来像每天"硬摊出一串定日待办——那正是 18 条误判的来历。
                    await _itemRepository.AddToDayAsync(title, SelectedDay,
                        completionMode: CompletionMode.PerOccurrence);
                    break;
                default:
                    await _itemRepository.AddToDayAsync(title, SelectedDay);
                    break;
            }

            // 清空输入框、形态退回默认、收起选择区，让用户可以直接接着输入下一条；
            // 形态必须重置，否则连着录第二条时会莫名其妙又变成一个截止日/每天。
            // 这种"添加后可以连续录入"的手感，决定了你会不会真的每天用它记录。
            NewTitle = string.Empty;
            NewItemShape = AddShape.Day;
            IsShapePickerOpen = false;

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
            // 为什么这里不能用 SetStatusAsync：勾完成的那一刻要顺手把
            // 这段计时结算成 ActualMinutes，那是 AI 判断"你做事要花多久"的唯一依据。
            var wasFloating = FloatingItems.Contains(item);
            var updated = await _itemRepository.ToggleDoneAsync(item.Id);

            if (updated is not null)
            {
                if (wasFloating)
                {
                    // 【常驻项提前完成，必须整页重查，不能就地改这一行】
                    // 一勾完成，它就不再满足「还悬着」：常驻带该把它收起，
                    // 而 GetDayListAsync 的 completedThatDay 分支会把它接进
                    // 「今天做完的事」——从「一直悬着」搬到「做到了」，
                    // 这次搬家是两个查询口径一起变的结果，就地 Update 做不到。
                    // 取消完成同理，它会从清单回到常驻带。
                    await LoadAsync();
                }
                else
                {
                    item.Update(updated);
                    UpdateSummary();
                }
            }
        }
        catch (Exception ex)
        {
            await ReportErrorAsync(ex);
        }
    }

    /// <summary>
    /// 启动 / 暂停：一个按钮来回切。
    ///
    /// 【为什么这条要把二级菜单里的东西挪出来】
    /// "开始做"是这个软件最重要的一次点击——
    /// 整个产品要解决的其实就是"启动不了"这一件事。
    /// 把它埋在菜单里第二层，等于给最重要的一步加了两下操作。
    ///
    /// 【为什么按下之后就地更新这一行，而不是整页重载】
    /// 点完立刻看到"暂停"两个字和走起来的数字，人才相信刚才那一下生效了。
    /// 而且整页重建会把滚动位置扔回顶部——连着点几条时它就会一直跟你作对。
    /// </summary>
    [RelayCommand]
    private async Task ToggleStartPauseAsync(ItemViewModel item)
    {
        try
        {
            var updated = await _itemRepository.ToggleStartPauseAsync(item.Id);

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
