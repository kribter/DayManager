using CommunityToolkit.Mvvm.ComponentModel;
using DayManager.App.Services;
using DayManager.Core.Entities;
using DayManager.Core.Enums;
using DayManager.Core.Planning;

namespace DayManager.App.ViewModels;

/// <summary>
/// 列表里"一行"对应的视图模型。
///
/// 【为什么不让界面直接绑定数据库中的 Item，而要再包一层】
/// 界面需要的东西和数据库存的东西经常不是一个东西：
///   数据库里存 Status = 2（一个整数），界面上要显示"已完成"和灰色文字；
///   数据库里存三个可能为空的时间字段，界面上要显示成一句话"逾期 3 天"。
/// 如果把这些格式化代码写进 XAML，XAML 会变得没法读；
/// 如果写进 Item 实体，Core 层就被界面细节污染了。
/// 所以中间加一层专门做"翻译"，这是 MVVM 里的 VM 的本职工作。
///
/// 【MVVM 三个字母分别是什么】
///   Model    = Item          数据本身，不知道界面的存在
///   View     = MainPage.xaml 界面本身，不含业务逻辑
///   ViewModel= 本类          把 Model 翻译成 View 能直接显示的样子，并处理用户操作
/// 三者分工的意义在于：界面改版不用动数据层，数据层加字段也不用重写界面。
/// </summary>
public partial class ItemViewModel : ObservableObject
{
    private Item _item;

    /// <summary>
    /// 这一行是不是「一直悬着、还没到截止日」那一带的。
    ///
    /// 【为什么由外面传进来，而不是自己根据 DueAt 判】
    /// 「算不算常驻」的判定已经在 Core 的 ItemPresence 里只有一份，
    /// 取数时就用它筛过了。这里再判一遍就是第二处口径，迟早不一致。
    /// 界面只负责「这一行该长什么样」，不负责「它属不属于这一带」。
    /// </summary>
    private readonly bool _isFloating;

    public ItemViewModel(Item item, bool isFloating = false)
    {
        _item = item;
        _isFloating = isFloating;
        Refresh();
    }

    /// <summary>底层数据。ViewModel 不隐藏它，方便必要时直接读原始字段。</summary>
    public Item Item => _item;

    public Guid Id => _item.Id;

    // 【下面这些带 [ObservableProperty] 的 partial 属性是 CommunityToolkit.Mvvm 的用法】
    // 你只负责声明属性本身，工具会在编译时自动生成它的实现——
    // 除了常规的取值赋之外，还会在值变化时发出"变了"的通知（INotifyPropertyChanged）。
    // 这个通知正是数据绑定的基础：属性值一变，绑了它的控件就自动刷新，
    // 不需要你手动写 Label.Text = xxx。
    // 手写这套通知每个属性要十几行样板代码，用这个特性一行搞定。
    //
    // 【为什么要写成 public partial 属性，而不是老教程里的"私有字段"写法】
    // 老写法是声明 private string title; 让工具去生成 Title 属性。
    // 但在 WinUI 3 / AOT 场景下它生成不了 WinRT 需要的封送代码，编译器会报警告（MVVMTK0045）。
    // 所以现在推荐这种"部分属性"写法：属性由你声明，实现由工具在编译时补全，
    // 看到 partial 就说明"另一半不在这个文件里"。

    [ObservableProperty]
    public partial string Title { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Subtitle { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string StatusText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsDone { get; set; }

    [ObservableProperty]
    public partial Color TitleColor { get; set; } = AppColors.Text;

    [ObservableProperty]
    public partial Color CheckColor { get; set; } = AppColors.Muted;

    /// <summary>
    /// 状态徽章要不要显示。待办时不显示——
    /// 每一条都挂个"待办"标签是纯噪音，徽章的价值在于标出**例外**。
    /// </summary>
    [ObservableProperty]
    public partial bool HasStatusBadge { get; set; }

    /// <summary>
    /// 最小启动步骤：「打开文档写个标题」这种两分钟能做完的第一步。
    ///
    /// 【为什么现在要把它显示出来】
    /// 这个字段从数据模型建好那天就在，AI 拆解时也一直给每条都填，
    /// 可它只在拆解结果页露过面——一旦落到日程清单里就再也看不见了。
    /// 于是实际效果是：拆完那会儿看得见"第一步"，过一天想动手时反而没了。
    /// **凡是算出来的东西，一定要显示出来**，不然跟没算一样。
    ///
    /// 【为什么不放在副标题那一行】
    /// 副标题是"什么时候做"（估时 / 截止 / 逾期），
    /// 第一步是"怎么开始"——两件事挤在一行会读成一句话，扫视时两边都丢。
    /// 它单独占一行，正好落在标题下方，也就是人判断"这条怎么下手"时看的地方。
    /// </summary>
    [ObservableProperty]
    public partial string StepText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool HasMinimalStep { get; set; }

    /// <summary>
    /// 这一条现在能不能开「先做 10 分钟」。
    ///
    /// 完成和放弃已经了结，再给一个"先做 10 分钟"是自相矛盾；
    /// 跳过过的任务正需要它——那正是"启动不了"的典型。
    /// </summary>
    [ObservableProperty]
    public partial bool CanFocus { get; set; }

    /// <summary>
    /// 状态徽章的颜色。
    ///
    /// 【为什么这里写死色值，而不是去 XAML 里取资源】
    /// 这个文件里 TitleColor / CheckColor 一直都是直接给 Color 对象，
    /// 保持一致比追求"单一来源"重要。代价是改配色时要改两处，
    /// 所以每个值都注明它对应 Colors.xaml 里的哪个语义色。
    /// </summary>
    [ObservableProperty]
    public partial Color StatusColor { get; set; } = AppColors.Muted;

    /// <summary>
    /// 徽章描边用的画刷。
    ///
    /// 【为什么不直接把 StatusColor 绑到 Border.Stroke 上】
    /// Stroke 是 Brush（画刷），不是 Color（颜色）。实测 MAUI 会在赋值时
    /// 帮你把 Color 转成 Brush，所以直接绑也能显示——
    /// 但那是靠框架的一次隐式转换，换版本、换平台都可能变。
    /// 这里在颜色变化时顺手生成同类型的画刷，绑定两端类型严丝合缝，
    /// 不依赖框架的隐式行为。
    /// </summary>
    [ObservableProperty]
    public partial Brush StatusBrush { get; set; } = new SolidColorBrush(AppColors.Muted);

    partial void OnStatusColorChanged(Color value) => StatusBrush = new SolidColorBrush(value);

    /// <summary>
    /// 标题的删除线。
    ///
    /// 【为什么完成和放弃都要划掉，而只靠变灰不够】
    /// 变灰是一个弱信号：在深色模式下灰字和次要说明文字的灰几乎一样，
    /// 扫一眼分不清"这条做完了"还是"这条是注释"。
    /// 删除线是唯一一个不需要读字就能看出来的"此事已了结"标记。
    ///
    /// 【为什么必须在每次 Refresh 开头先清成"无"】
    /// 它原先只在"完成"和"不做了"两个分支里赋值，其余分支根本不碰。
    /// 后果是具体的一幕：勾上完成 → 划掉；再点一下取消勾选 → **删除线还在**。
    /// （从「更多」菜单里改状态看不出这个毛病，因为那条路会整页重查、
    /// 重新 new 一行 ViewModel，属性回到默认值——同一个 bug 换个入口就藏起来了。）
    ///
    /// 这类 bug 的形态是固定的：**某个属性只在部分分支里被设置**，
    /// 一旦走进另一个分支，它就留在上一次的值上，不报错、也不容易被发现。
    /// 所以凡是"只有少数状态要显示、其余状态不该显示"的显示属性，
    /// 一律先清到默认值，再按需设——不要指望别的分支替你清。
    /// </summary>
    [ObservableProperty]
    public partial TextDecorations TitleDecorations { get; set; }

    /// <summary>
    /// 这一条现在是不是正在走表。
    ///
    /// 【为什么判定必须把"进行中"和"StartedAt 有值"两个数一起看】
    /// 因为暂停是用"把 StartedAt 清空"来表示的（见 ItemRepository.ToggleStartPauseAsync）：
    /// 状态照样是进行中，只是这会儿没在计时。
    /// 只看状态的话，暂停中的条目会被当成"正在计时"，于是继续往上加时间。
    /// </summary>
    public bool IsCounting =>
        _item.Status == ItemStatus.InProgress && _item.StartedAt is not null;

    /// <summary>是不是"开始过、但现在停着"。</summary>
    public bool IsPaused =>
        _item.Status == ItemStatus.InProgress && _item.StartedAt is null;

    /// <summary>
    /// 启动按钮上写什么：启动 / 暂停 / 继续。
    ///
    /// 【为什么按钮要换字而不是换个图标】
    /// 同一个位置在两种时刻做两件事，靠图标区分要人先记住图标的含义；
    /// 换成字，"点下去会发生什么"就写在那儿了——这也是把二级菜单挪出来的前提：
    /// 挪出来之后每个按钮都得自己说清楚自己是干什么的。
    /// </summary>
    public string StartButtonText =>
        IsCounting ? "暂停" : IsPaused ? "继续" : "启动";

    /// <summary>
    /// 这一条还能不能点启动。
    ///
    /// 完成和「不做了」不给按钮：那两件都已经了结，再给个开始是自相矛盾。
    /// </summary>
    public bool CanStartPause =>
        _item.Status is ItemStatus.Pending or ItemStatus.Skipped or ItemStatus.InProgress;

    /// <summary>
    /// 到目前为止累计花了多少分钟。
    ///
    /// 【为什么不把它存成一个固定值，而是每次问都重算】
    /// 正在计时的时候这个值每分钟都在变，
    /// 存下来就得配一套机制去同步它——不如随问随算，永远不会过期。
    /// 算法在 Core 里，跟暂停结算、完成回填共用同一份（见 Core/Planning/Elapsed）。
    ///
    /// 【为什么做完之后要拿 CompletedAt 当截止】
    /// 做完之后 StartedAt 还留着（那是有价值的历史），
    /// 还拿"现在"去算的话，这条的用时会随着你放得越久越长——
    /// 昨天做完的事，今天看能用了一天。
    /// </summary>
    public int ElapsedMinutes =>
        Elapsed.TotalMinutes(
            _item.ActualMinutes,
            _item.StartedAt,
            _item.Status == ItemStatus.Done && _item.CompletedAt is not null
                ? _item.CompletedAt.Value
                : DateTimeOffset.Now);

    /// <summary>
    /// 只把"走表相关的几个值"通知界面刷新一次。
    ///
    /// 【为什么这些 computed 属性要手动发通知】
    /// 它们是每次取值都重新算一遍的只读属性，底下没有存值的字段，
    /// 所以框架无从知道"什么时候变了"。Refresh 之后、每次走字之后都得叫一次，
    /// 否则界面上那句"已进行 12 分钟"会一直停在第一次显示的数字上。
    /// </summary>
    private void NotifyTiming()
    {
        OnPropertyChanged(nameof(ElapsedMinutes));
        OnPropertyChanged(nameof(IsCounting));
        OnPropertyChanged(nameof(IsPaused));
        OnPropertyChanged(nameof(StartButtonText));
        OnPropertyChanged(nameof(CanStartPause));
        OnPropertyChanged(nameof(ShowTimerButton));
    }

    // ---------- 大任务详情页：这一行在树里的位置 ----------
    //
    // 日程页不用这几个（默认第 0 层、不是容器、没有阶段进度文案），
    // 所以同一行 ViewModel 能在两个页面复用，不必为详情页另造一套模板。
    // 树怎么组装是 Core/Planning/ProjectTree 的纯函数，这里只负责「摆出来」。

    /// <summary>第几层：0 = 顶层阶段，或直接挂在大任务下的叶子。</summary>
    [ObservableProperty]
    public partial int Depth { get; set; }

    /// <summary>整行左缩进。直接绑 Grid.Padding，不需要值转换器。</summary>
    public Thickness RowPadding => new(4 + Depth * 16, 8, 4, 8);

    partial void OnDepthChanged(int value) => OnPropertyChanged(nameof(RowPadding));

    /// <summary>
    /// 容器（阶段性目标）：没有完成按钮、不进分母，只显示下级进度。
    /// 是不是容器由 Core 的 ItemAxes.IsContainer 判，这里不另写一遍。
    /// </summary>
    [ObservableProperty]
    public partial bool IsContainer { get; set; }

    public bool IsLeaf => !IsContainer;
    public bool ShowTimerButton => !IsContainer && CanStartPause;
    public bool HasStageProgress => IsContainer && !string.IsNullOrEmpty(StageProgressText);

    partial void OnIsContainerChanged(bool value)
    {
        OnPropertyChanged(nameof(IsLeaf));
        OnPropertyChanged(nameof(ShowTimerButton));
        OnPropertyChanged(nameof(HasStageProgress));
    }

    /// <summary>容器行的下级进度，例如「1 / 3 件完成」。叶子行留空。</summary>
    [ObservableProperty]
    public partial string StageProgressText { get; set; } = string.Empty;

    partial void OnStageProgressTextChanged(string value) =>
        OnPropertyChanged(nameof(HasStageProgress));

    /// <summary>
    /// 每过一小会儿叫一次：重算那句"已进行几分钟"，值变了就让绑定自己刷新。
    ///
    /// 【为什么不等用户操作再刷】
    /// 计时数字存在的全部意义就是"它在走"。
    /// 一个不会自己动的秒表等于没有计时，人只会去别处看时间。
    /// </summary>
    public void RefreshElapsed()
    {
        Subtitle = BuildSubtitle();
        NotifyTiming();
    }

    /// <summary>
    /// 用新的数据替换内部实体，并重算所有展示用的属性。
    /// 状态改变后调用它，界面上对应的那一行就会自动更新。
    /// </summary>
    public void Update(Item item)
    {
        _item = item;
        Refresh();
    }

    /// <summary>
    /// 正文文字的颜色必须跟着当前主题走。
    ///
    /// 【为什么不能写死 Colors.Black】
    /// 深色模式下黑色文字会直接消失在黑背景里。
    /// 这是新手做界面最容易踩的坑之一：本机看着没问题，换个主题就瞎。
    ///
    /// 【为什么走 AppColors 而不是在这里判断 RequestedTheme】
    /// 这个软件允许用户在软件内部选深浅（多数人的系统是浅色，
    /// 但编辑器、浏览器都被他自己设成了深色），
    /// 只看系统的 RequestedTheme 会跟用户在软件里选的结果不一致。
    /// 判断逻辑收在 AppColors 里，这里只管说"我要正文色"。
    /// </summary>
    private static Color PrimaryTextColor => AppColors.Text;

    private void Refresh()
    {
        // 每个显示属性在每一轮都要有一个确定的值：
        // 尤其是"只有部分状态才显示"的那些（删除线、徽章），先清干净，
        // 否则上一次的残留会留到下一次——详见 TitleDecorations 的注释。
        TitleDecorations = TextDecorations.None;
        HasStatusBadge = false;
        HasMinimalStep = false;
        CanFocus = false;
        StepText = string.Empty;

        Title = _item.Title;
        IsDone = _item.Status == ItemStatus.Done;

        // 下面每个分支还要把徽章的两个属性（显示与否 / 颜色）一起设：
        // 漏掉 HasStatusBadge 的表现是"颜色对了但看不见"，
        // 漏掉 StatusColor 的表现是"看见了但是灰色"，两种都不会报错。
        switch (_item.Status)
        {
            case ItemStatus.Pending:
                StatusText = "待办";
                HasStatusBadge = false;
                TitleColor = PrimaryTextColor;
                CheckColor = AppColors.Muted;
                break;

            case ItemStatus.InProgress:
                StatusText = "进行中";
                HasStatusBadge = true;
                StatusColor = AppColors.Progress;
                TitleColor = PrimaryTextColor;
                CheckColor = AppColors.Progress;
                break;

            case ItemStatus.Done:
                StatusText = "完成";
                HasStatusBadge = true;
                StatusColor = AppColors.Done;
                // 完成的事项降低视觉权重，让视线自然落到还剩下的事情上。
                // 用 AppColors.Faint 而不是 Colors.Gray：深色模式下的灰太亮，
                // 划掉的和没划的看着差不多，等于没划。
                TitleColor = AppColors.Faint;
                CheckColor = AppColors.Done;
                TitleDecorations = TextDecorations.Strikethrough;
                break;

            case ItemStatus.Abandoned:
                StatusText = "不做了";
                HasStatusBadge = true;
                StatusColor = AppColors.Muted;
                TitleColor = AppColors.Faint;
                CheckColor = AppColors.Muted;
                TitleDecorations = TextDecorations.Strikethrough;
                break;

            case ItemStatus.Skipped:
                // 【为什么走到这里它就不是"跳过"，而是正常待办】
                // "跳过"只对跳过发生的那一天有效，这是个查询层面的保证：
                // 当天跳过的记录根本不会出现在这一天的清单里（见 ItemRepository.IsHiddenOn）。
                // 所以凡是能出现在清单上的 Skipped，都是**已经回来**的——
                // 昨天说"今天再做"，今天打开就看见它，那它就是今天该做的事。
                // 要是这时还顶着"已跳过"的灰色标记，用户会以为自己昨天已经处理过它了，
                // 于是一个本来要做的东西被安静地晾在那儿。
                //
                // 跳过次数不是用来责备人的，是给"这事是不是根本不该做"留线索：
                // 反复跳过 = 启动成本可能远高于预估，交给 AI 主动提出来谈。
                //
                // 【跳过次数为什么要显示出来】
                // 它同时是两个信号：
                //   对用户——"这件事我已经推了三次了"，是个不带指责的提醒；
                //   对 AI——反复跳过是"启动成本远高于预估"的最强线索，
                //          后面要靠它来主动提出"是不是该拆小一点"。
                // 隐藏它的代价是：这个线索只有数据库知道，界面和 AI 都不知道。
                HasStatusBadge = _item.SkipCount > 1;
                StatusColor = AppColors.Muted;
                StatusText = _item.SkipCount > 1
                    ? $"跳过过 {_item.SkipCount} 次"
                    : "待办";
                TitleColor = PrimaryTextColor;
                CheckColor = AppColors.Muted;
                break;
        }

        Subtitle = BuildSubtitle();

        // 【第一步只在"还没了结"的时候出现】
        // 已经做完、或者已经决定不做的事，不需要再教人怎么开头。
        HasMinimalStep = !string.IsNullOrWhiteSpace(_item.MinimalStep)
                         && _item.Status is not (ItemStatus.Done or ItemStatus.Abandoned);

        StepText = HasMinimalStep ? $"第一步：{_item.MinimalStep}" : string.Empty;

        // 容器（ByChildren）没有完成按钮，守住类（Never）不判成败——
        // 「先睡 10 分钟」那种话就是从「还能点一下」这条路冒出来的。
        // 判定走 Core 的 ItemAxes，跟离线兜底、AI 提示词同一份判据，不在这里另写一遍。
        // 主界面的清单和常驻带本来就不收这两类（GetDayListAsync / GetFloatingDeadlinesAsync
        // 已过滤），这里是第二道保险：大任务详情页会直接列项目下的全部 item。
        CanFocus = (_item.Status is ItemStatus.Pending or ItemStatus.Skipped or ItemStatus.InProgress)
                   && ItemAxes.CanNudge(_item, DateOnly.FromDateTime(DateTime.Now));

        // 上面那几个 derived 属性（用时、按钮文案）底层没有存值字段，
        // 换了新实体之后必须自己喊一声，否则按钮上的字还停在上一轮。
        NotifyTiming();
    }

    /// <summary>
    /// 把三个可能为空的时间字段，翻译成一句人话。
    ///
    /// 【刻意没有显示的东西】
    /// CreatedAt 到 StartedAt 的间隔（启动延迟）虽然已经记录在库里了，但这里不显示。
    /// 因为它同时是一个会产生压力的数字：如果每天盯着"我又花了 40 分钟才肯动手"，
    /// 反而会强化挫败感。它应该出现在周/月的趋势回顾里（那时候是"这周比上周快了 8 分钟"），
    /// 而不是出现在每天的待办列表里。
    /// </summary>
    private string BuildSubtitle()
    {
        var parts = new List<string>();

        // 预计耗时放在最前面：它是判断"这一条值不值得现在做"的第一信息，
        // 比几点截止更影响当下的决策。
        //
        // 【为什么这一版要把它加上】
        // AI 拆解时给每条都估了时，也确实写进了数据库，但界面上一个字没显示。
        // 结果是用户在今日清单里只看到一串标题，
        // 完全不知道今天 collectively 要花多久——而"今天要做多久"恰恰是他决定要不要开始的关键。
        if (_item.EstimateMinutes is > 0)
        {
            parts.Add(DurationText.Format(_item.EstimateMinutes.Value));
        }

        // 【显示时间前一定要 ToLocalTime()】
        // 数据库里存的是 UTC 毫秒，读出来的 DateTimeOffset 偏移量是 0。
        // 直接格式化它，你看到的就是 UTC 时间——北京时间会整整少 8 个小时。
        // 这条规则很简单，但一旦漏掉就会得到"看起来对但差几小时"的结果，非常难查。
        var startLocal = _item.StartAt?.ToLocalTime();
        var dueLocal = _item.DueAt?.ToLocalTime();
        var completedLocal = _item.CompletedAt?.ToLocalTime();

        if (_item.Status == ItemStatus.Done)
        {
            // 【为什么做完的事一定要把实际用时写出来】
            // 这是"你做事到底要花多久"的唯一现场记录，也是 AI 下次排程的唯一依据
            // （没有它，AI 只能照你的乐观估计排，于是天天排不下）。
            // 上一版这里只有"完成于 16:43"，做完之后连自己花了多久都不知道。
            if (_item.ActualMinutes is > 0)
            {
                parts.Add($"用了 {DurationText.Format(_item.ActualMinutes.Value)}");
            }

            if (completedLocal.HasValue)
            {
                parts.Add($"完成于 {completedLocal.Value:HH:mm}");
            }
        }
        else if (_item.Status == ItemStatus.InProgress)
        {
            // 【为什么已经在做的事要写出"走了多久"】
            // "已经开了头"这件事本身就值钱：它把最难的"要不要开始"
            // 变成了"要不要停下"。数字在那儿自己变大，
            // 半途而废的代价不用谁提醒，自己看得见。
            var elapsed = ElapsedMinutes;

            if (IsCounting)
            {
                parts.Add(elapsed > 0 ? $"已进行 {elapsed} 分钟" : "刚开始");
            }
            else
            {
                // 暂停态：表停了，但之前各段都还在。
                // 只写"已暂停"会让人以为前面那段白做了，所以把累计也带上。
                parts.Add(elapsed > 0 ? $"已暂停 · 累计 {elapsed} 分钟" : "已暂停");
            }
        }

        if (startLocal.HasValue)
        {
            parts.Add($"{startLocal.Value:HH:mm} 开始");
        }
        else if (dueLocal.HasValue)
        {
            var today = DateOnly.FromDateTime(DateTime.Now);
            var dueDay = DateOnly.FromDateTime(dueLocal.Value.DateTime);

            if (_isFloating && dueDay > today && _item.DueAt is { } floatingDue)
            {
                // 【常驻项显示"还剩几天"，绝不显示"逾期"】
                // 它的截止日在未来，"逾期"是错的，而且会重新变成每天的压力——
                // 正是这一形态要消除的东西。剩余天数由 Core 算好（不自己减日期），
                // 跟 AI 提示词里那条用的是同一个函数，界面和 AI 说同一个数。
                parts.Add(ItemPresence.DeadlineLine(floatingDue, today));
            }
            else if (dueDay < today)
            {
                // 逾期天数必须显式说出来。这是"欠账清单"，
                // 让它自动滚到今天还不够，必须让用户知道欠了多久。
                // 【已完成的不算逾期】"逾期"描述的是还欠着，做完的事已经还了。
                // 翻历史日期时尤其扎眼：11-17 做完的任务，不该因为截止日是 11-15
                // 就在副标题里顶着"逾期 2 天"。其余状态（含进行中/跳过/放弃）保持原样。
                // 【不判成败的不算逾期】容器由子项汇总、守住类永不进分母，
                // 给它们挂「逾期 N 天」等于把不该产生压力的事说成欠账。
                if (_item.Status != ItemStatus.Done
                    && ItemAxes.VerdictOf(_item, today) == ItemAxes.Verdict.Judged
                    && _item.CompletionMode != CompletionMode.ByChildren)
                {
                    parts.Add($"逾期 {today.DayNumber - dueDay.DayNumber} 天");
                }
            }
            else if (dueDay == today)
            {
                // 截止时间是 23:59 的记录说明用户还没给它安排具体时间，不该显示成"23:59 截止"
                parts.Add(dueLocal.Value is { Hour: 23, Minute: 59 } ? "尚未安排时间" : $"{dueLocal.Value:HH:mm} 截止");
            }
            else
            {
                parts.Add($"{dueLocal.Value:M月d日} 截止");
            }
        }
        else
        {
            parts.Add("尚未安排时间");
        }

        return string.Join(" · ", parts);
    }
}
