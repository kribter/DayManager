using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DayManager.Core.Enums;
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

    public MainViewModel(ItemRepository itemRepository)
    {
        _itemRepository = itemRepository;
        DateText = DateTime.Now.ToString("M月d日 dddd", new CultureInfo("zh-CN"));
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

    [ObservableProperty]
    public partial string DateText { get; set; } = string.Empty;

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

    // ================= 命令 =================

    /// <summary>
    /// [RelayCommand] 会自动生成一个叫 LoadCommand 的属性（去掉方法名结尾的 Async）。
    /// XAML 里就写 Command="{Binding LoadCommand}"。
    /// 手写这个命令本来需要定义 ICommand 字段、写 CanExecute、还要处理同步异步，二三十行起步。
    /// </summary>
    [RelayCommand]
    private async Task LoadAsync()
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        try
        {
            var items = await _itemRepository.GetTodayListAsync();

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
        finally
        {
            IsBusy = false;
        }
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
            // 显式写成 DateTimeOffset? 而不是用 var，
            // 是因为三元表达式里一个是 DateTimeOffset、一个是 null，编译器推断不出统一类型。
            // 加上 (DateTimeOffset?) 这个强制转换，编译器就知道两边都按可空类型处理了。
            DateTimeOffset? startAt = UseSpecificTime ? CombineToday(NewTime) : null;

            await _itemRepository.AddAsync(NewTitle.Trim(), startAt);

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
    /// 右侧 ⋯ 菜单：把不常用的操作收在这里，保持主界面干净。
    /// </summary>
    [RelayCommand]
    private async Task ShowActionsAsync(ItemViewModel item)
    {
        var page = CurrentPage;
        if (page is null)
        {
            return;
        }

        // 菜单项按当前状态动态生成：已经完成的条目不该再显示"标记完成"。
        var choices = new List<string>();
        if (item.Item.Status != ItemStatus.InProgress && item.Item.Status != ItemStatus.Done)
        {
            choices.Add("标记为进行中");
        }

        if (item.Item.Status != ItemStatus.Done)
        {
            choices.Add("标记完成");
        }
        else
        {
            choices.Add("改回待办");
        }

        choices.Add("重命名");
        choices.Add("删除");

        var action = await page.DisplayActionSheetAsync(item.Title, "取消", null, choices.ToArray());

        try
        {
            switch (action)
            {
                case "标记为进行中":
                    await ChangeStatusAsync(item, ItemStatus.InProgress);
                    break;

                case "标记完成":
                    await ChangeStatusAsync(item, ItemStatus.Done);
                    break;

                case "改回待办":
                    await ChangeStatusAsync(item, ItemStatus.Pending);
                    break;

                case "重命名":
                    var newName = await page.DisplayPromptAsync("重命名", "改成：", initialValue: item.Title);
                    if (!string.IsNullOrWhiteSpace(newName))
                    {
                        await _itemRepository.RenameAsync(item.Id, newName);
                        await LoadAsync();
                    }

                    break;

                case "删除":
                    // 删除是唯一不可逆的选项，所以多问一句。
                    // 而且这里问的是"确定删掉「标题」"，带上具体内容，
                    // 比干巴巴的"确定删除吗"更能防止误操作。
                    var confirmed = await page.DisplayAlertAsync(
                        "删除", $"确定删掉「{item.Title}」？", "删除", "取消");
                    if (confirmed)
                    {
                        await _itemRepository.SoftDeleteAsync(item.Id);
                        await LoadAsync();
                    }

                    break;
            }
        }
        catch (Exception ex)
        {
            await ReportErrorAsync(ex);
        }
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
            ? "今天还没有安排"
            : $"已完成 {done} / {total} 件";
    }

    /// <summary>
    /// 把一个"几点几分"拼成今天的具体时刻。
    /// 用户勾选了"指定开始时间"时才会用到。
    /// </summary>
    private static DateTimeOffset CombineToday(TimeSpan time)
    {
        var todayPart = DateOnly.FromDateTime(DateTime.Now).ToDateTime(TimeOnly.FromTimeSpan(time));
        var offset = TimeZoneInfo.Local.GetUtcOffset(todayPart);
        return new DateTimeOffset(todayPart, offset);
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
