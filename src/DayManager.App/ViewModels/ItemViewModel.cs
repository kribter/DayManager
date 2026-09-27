using CommunityToolkit.Mvvm.ComponentModel;
using DayManager.Core.Entities;
using DayManager.Core.Enums;

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
        Title = _item.Title;
        IsDone = _item.Status == ItemStatus.Done;

        switch (_item.Status)
        {
            case ItemStatus.Pending:
                StatusText = "待办";
                TitleColor = PrimaryTextColor;
                CheckColor = Colors.Gray;
                break;

            case ItemStatus.InProgress:
                StatusText = "进行中";
                TitleColor = PrimaryTextColor;
                CheckColor = Colors.DarkOrange;
                break;

            case ItemStatus.Done:
                StatusText = "完成";
                // 完成的事项降低视觉权重，让视线自然落到还剩下的事情上
                TitleColor = Colors.Gray;
                CheckColor = Colors.ForestGreen;
                break;

            case ItemStatus.Cancelled:
                StatusText = "已取消";
                TitleColor = Colors.Gray;
                CheckColor = Colors.Gray;
                break;
        }

        Subtitle = BuildSubtitle();
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
