using CommunityToolkit.Mvvm.ComponentModel;
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

    public ItemViewModel(Item item)
    {
        _item = item;
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
    public partial Color TitleColor { get; set; } = Colors.Black;

    [ObservableProperty]
    public partial Color CheckColor { get; set; } = Colors.Gray;

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
    public partial Color StatusColor { get; set; } = Colors.Gray;

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
    public partial Brush StatusBrush { get; set; } = new SolidColorBrush(Colors.Gray);

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
    /// 用新的数据替换内部实体，并重算所有展示用的属性。
    /// 状态改变后调用它，界面上对应的那一行就会自动更新。
    /// </summary>
    public void Update(Item item)
    {
        _item = item;
        Refresh();
    }

    /// <summary>
    /// 正文文字的颜色必须随系统主题走。
    ///
    /// 【为什么不能写死 Colors.Black】
    /// 你开着 Windows 的深色模式时，黑色文字会直接消失在黑背景里。
    /// 这是新手做界面最容易踩的坑之一：本机看着没问题，换个主题就瞎。
    /// 所以凡是"跟随正文的颜色"，都要按当前主题取值，不能用固定色。
    /// </summary>
    private static Color PrimaryTextColor =>
        Application.Current?.RequestedTheme == AppTheme.Dark ? Colors.White : Colors.Black;

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
                CheckColor = Colors.Gray;
                break;

            case ItemStatus.InProgress:
                StatusText = "进行中";
                HasStatusBadge = true;
                StatusColor = Color.FromArgb("#C07820"); // Colors.xaml: ProgressOrange
                TitleColor = PrimaryTextColor;
                CheckColor = Colors.DarkOrange;
                break;

            case ItemStatus.Done:
                StatusText = "完成";
                HasStatusBadge = true;
                StatusColor = Color.FromArgb("#2E7D5B"); // Colors.xaml: DoneGreen
                // 完成的事项降低视觉权重，让视线自然落到还剩下的事情上
                TitleColor = Colors.Gray;
                CheckColor = Colors.ForestGreen;
                TitleDecorations = TextDecorations.Strikethrough;
                break;

            case ItemStatus.Abandoned:
                StatusText = "不做了";
                HasStatusBadge = true;
                StatusColor = Color.FromArgb("#8A8A8A"); // Colors.xaml: MutedText
                TitleColor = Colors.Gray;
                CheckColor = Colors.Gray;
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
                StatusColor = Color.FromArgb("#8A8A8A"); // Colors.xaml: MutedText
                StatusText = _item.SkipCount > 1
                    ? $"跳过过 {_item.SkipCount} 次"
                    : "待办";
                TitleColor = PrimaryTextColor;
                CheckColor = Colors.Gray;
                break;
        }

        Subtitle = BuildSubtitle();

        // 【第一步只在"还没了结"的时候出现】
        // 已经做完、或者已经决定不做的事，不需要再教人怎么开头。
        HasMinimalStep = !string.IsNullOrWhiteSpace(_item.MinimalStep)
                         && _item.Status is not (ItemStatus.Done or ItemStatus.Abandoned);

        StepText = HasMinimalStep ? $"第一步：{_item.MinimalStep}" : string.Empty;

        CanFocus = _item.Status
            is ItemStatus.Pending or ItemStatus.Skipped or ItemStatus.InProgress;
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
        var startedLocal = _item.StartedAt?.ToLocalTime();

        if (_item.Status == ItemStatus.Done && completedLocal.HasValue)
        {
            parts.Add($"完成于 {completedLocal.Value:HH:mm}");
        }
        else if (_item.Status == ItemStatus.InProgress && startedLocal.HasValue)
        {
            // "已经开始多久了"比"还剩多久"更能让人想继续做完
            // （两个 DateTimeOffset 相减得到 TimeSpan，相减本身与时区无关，不涉及 UTC 问题）
            var elapsed = DateTimeOffset.Now - _item.StartedAt!.Value;
            parts.Add($"已进行 {(int)elapsed.TotalMinutes} 分钟");
        }

        if (startLocal.HasValue)
        {
            parts.Add($"{startLocal.Value:HH:mm} 开始");
        }
        else if (dueLocal.HasValue)
        {
            var today = DateOnly.FromDateTime(DateTime.Now);
            var dueDay = DateOnly.FromDateTime(dueLocal.Value.DateTime);

            if (dueDay < today)
            {
                // 逾期天数必须显式说出来。这是"欠账清单"，
                // 让它自动滚到今天还不够，必须让用户知道欠了多久。
                parts.Add($"逾期 {today.DayNumber - dueDay.DayNumber} 天");
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
