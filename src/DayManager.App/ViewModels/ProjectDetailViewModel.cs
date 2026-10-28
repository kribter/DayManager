using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using DayManager.App.Messages;
using DayManager.App.Services;
using DayManager.Core.Entities;
using DayManager.Core.Enums;
using DayManager.Data.Repositories;

namespace DayManager.App.ViewModels;

/// <summary>
/// 一个大任务的详情页：它下面所有子任务的清单。
///
/// 【为什么要有这一页，光有列表页不够吗】
/// 列表页给的是"做到 60%"，那是总结，不指向任何动作。
/// 打开这一页要回答的是"那我接下来该做哪一条"。
/// 差一个层次：百分比让人满意或者焦虑，清单让人动手。
///
/// 【为什么子任务可以打勾，项目本身没有完成按钮】
/// 需求 2.4：大任务只是容器。底下八条只做了三条，
/// 上面却显示"已完成"，那就是一个假的正反馈——
/// 而假的正反馈比没有反馈更伤，它会让人在真的没做完时以为做完了。
/// </summary>
public partial class ProjectDetailViewModel : ObservableObject
{
    private readonly ProjectRepository _projects;
    private readonly ItemRepository _items;
    private readonly ItemActionService _actions;

    private Guid _projectId;

    public ProjectDetailViewModel(
        ProjectRepository projects,
        ItemRepository items,
        ItemActionService actions)
    {
        _projects = projects;
        _items = items;
        _actions = actions;

        // AI 在对话里给这个项目加了任务，这一页要跟着变。
        WeakReferenceMessenger.Default.Register<ItemsChangedMessage>(
            this,
            (_, _) => _ = LoadCommand.ExecuteAsync(null));
    }

    public ObservableCollection<ItemViewModel> Items { get; } = new();

    /// <summary>
    /// 导航带过来的项目 id。
    ///
    /// 【为什么类型是 string 而不是 Guid】
    /// Shell 的查询参数（?projectId=xxx）在 URL 里只能是字符串，
    /// QueryProperty 会直接赋值，类型不对会抛异常。
    /// 所以这里收字符串，再自己转。
    /// </summary>
    [ObservableProperty]
    public partial string ProjectIdText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Name { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string ProgressText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial double ProgressRatio { get; set; }

    [ObservableProperty]
    public partial string TargetText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    /// <summary>输入框里正在打的子任务标题。</summary>
    [ObservableProperty]
    public partial string NewTitle { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsEmpty { get; set; } = true;

    partial void OnProjectIdTextChanged(string value)
    {
        if (Guid.TryParse(value, out var id))
        {
            _projectId = id;

            // 不能在属性 setter 里 await（属性没有异步版本），
            // 用 _ = 触发一次加载：这一页进来就是要把内容显示出来，
            // 早一秒晚一秒不影响正确性。
            _ = LoadCommand.ExecuteAsync(null);
        }
    }

    [RelayCommand]
    private async Task LoadAsync()
    {
        if (IsBusy || _projectId == Guid.Empty)
        {
            return;
        }

        IsBusy = true;
        try
        {
            var items = await _projects.GetItemsAsync(_projectId);

            Items.Clear();
            foreach (var item in items)
            {
                Items.Add(new ItemViewModel(item));
            }

            IsEmpty = Items.Count == 0;
            UpdateProgress();

            // 项目名单独取一次：GetItemsAsync 只给了任务，没带项目实体。
            var summary = (await _projects.GetActiveWithProgressAsync())
                .FirstOrDefault(x => x.Id == _projectId);

            if (summary is not null)
            {
                Name = summary.Name;
                TargetText = BuildTargetText(summary.TargetDate);
            }
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

    /// <summary>
    /// 打勾 / 取消打勾。
    ///
    /// 【为什么这里不重新查数据库】
    /// 打勾只影响这一行，重查整个列表会让滚动位置跳回去，
    /// 而"勾完一条想接着勾下一条"是最常见的连续动作。
    /// 只更新这一行，列表纹丝不动。
    /// </summary>
    [RelayCommand]
    private async Task ToggleCompleteAsync(ItemViewModel item)
    {
        try
        {
            var updated = await _items.SetStatusAsync(
                item.Id,
                item.IsDone ? ItemStatus.Pending : ItemStatus.Done);

            if (updated is not null)
            {
                item.Update(updated);
                UpdateProgress();
            }
        }
        catch (Exception ex)
        {
            await ReportErrorAsync(ex);
        }
    }

    /// <summary>
    /// 在大任务下自己加一条子任务。
    ///
    /// 【为什么这个入口必须存在】
    /// 大任务页原先只能看、只能删：加子任务只能回对话页让 AI 建，或者去日程页加完再想办法挂过来
    /// （实际是挂不过来的，日程页加的东西不属于任何项目）。
    /// 后果是"自己开一个容器"这条路只走了一半——能建壳，装不进去东西。
    /// 而 AI 不可用的时候（离线、后端没起），这条路就彻底断了。
    ///
    /// 【为什么加完默认排在今天】
    /// 沿用 <see cref="ItemRepository.AddAsync"/> 那条老规矩：没有具体时间的待办会被无限期推迟。
    /// 挂到今天，它今晚回顾时会逼你面对它——要么做，要么改期，要么删，不能装看不见。
    /// 代价是它会立刻出现在今天的日程清单里；这是刻意的，不是疏漏。
    ///
    /// 【为什么要广播 ItemsChangedMessage】
    /// 它会进今天的清单。不广播的话，切回日程页看到的是加之前的样子，
    /// 用户会以为"刚才那一条没加上"。
    /// </summary>
    [RelayCommand]
    private async Task AddSubtaskAsync()
    {
        if (string.IsNullOrWhiteSpace(NewTitle) || _projectId == Guid.Empty)
        {
            return;
        }

        try
        {
            await _items.AddAsync(NewTitle.Trim(), projectId: _projectId);

            // 不清输入框内容的理由和日程页一致：加完之后通常还要接着加下一条。
            NewTitle = string.Empty;

            await LoadAsync();
            WeakReferenceMessenger.Default.Send(new ItemsChangedMessage());
        }
        catch (Exception ex)
        {
            await ReportErrorAsync(ex);
        }
    }

    /// <summary>子任务的"更多"菜单，和日程页共用同一套。</summary>
    [RelayCommand]
    private async Task ShowActionsAsync(ItemViewModel item)
    {
        await _actions.ShowMenuAsync(item, LoadAsync);
    }

    /// <summary>
    /// 返回列表页。
    ///
    /// 【为什么这一页要自己做一个返回按钮】
    /// 系统导航栏那个返回箭头太小、太浅，而这一页没有别的出口——
    /// 用过一次被困住的经历之后，这个按钮必须显眼。位置在标题旁边（见 XAML）。
    ///
    /// 【为什么兜了两层】
    /// GoToAsync("..") 是常规路径；万一它失败（路由状态被别的导航改过），
    /// 再退一步用导航栈的 Pop。两次都不成才报错。
    /// 出口这种东西宁可多写三行，也不能只有一条路。
    /// </summary>
    [RelayCommand]
    private async Task BackAsync()
    {
        try
        {
            await Shell.Current.GoToAsync("..");
        }
        catch
        {
            try
            {
                await Shell.Current.Navigation.PopAsync();
            }
            catch (Exception ex)
            {
                await ReportErrorAsync(ex);
            }
        }
    }

    private void UpdateProgress()
    {
        //
        // 口径和列表页保持一致：放弃的不算进分母。
        // 两处算法不一样的话，同一个项目在列表页显示 60%、进来显示 75%，
        // 用户只会觉得这软件算不明白。
        //
        var active = Items.Count(x => x.Item.Status != ItemStatus.Abandoned);
        var done = Items.Count(x => x.IsDone);

        ProgressRatio = active == 0 ? 0 : (double)done / active;
        ProgressText = active == 0
            ? "这个项目还没有任务"
            : $"{done} / {active} 件完成";
    }

    private static string BuildTargetText(DateTimeOffset? targetDate)
    {
        if (targetDate is null)
        {
            return "还没定什么时候做完";
        }

        var today = DateOnly.FromDateTime(DateTime.Now);
        var target = DateOnly.FromDateTime(targetDate.Value.ToLocalTime().DateTime);
        var days = target.DayNumber - today.DayNumber;

        return days switch
        {
            > 0 => $"还剩 {days} 天",
            0 => "今天就是最后一天",
            _ => $"已过期 {-days} 天"
        };
    }

    private static Page? CurrentPage =>
        Application.Current?.Windows.Count > 0 ? Application.Current.Windows[0].Page : null;

    private static async Task ReportErrorAsync(Exception ex)
    {
        var page = CurrentPage;
        if (page is not null)
        {
            await page.DisplayAlertAsync("出错了", ex.Message, "好");
        }
    }
}
