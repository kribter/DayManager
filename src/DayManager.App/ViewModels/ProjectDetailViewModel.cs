using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using DayManager.App.Messages;
using DayManager.App.Services;
using DayManager.Core.Entities;
using DayManager.Core.Enums;
using DayManager.Core.Planning;
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

    /// <summary>
    /// 目标日期的**原值**（本地时间，日期粒度）。
    ///
    /// 【为什么除了 TargetText 还要留一份】
    /// TargetText 是给人看的那句"还剩 12 天"，已经丢掉了具体是哪一天。
    /// 改日期的时候日历要停在原来那天上（在这天上微调比重挑一次省事），
    /// 只有"还剩几天"这个数字推不回日期。列表页的卡片也是同样的理由留了一份。
    /// </summary>
    [ObservableProperty]
    public partial DateTime? TargetDate { get; set; }

    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    /// <summary>输入框里正在打的子任务标题。</summary>
    [ObservableProperty]
    public partial string NewTitle { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsEmpty { get; set; } = true;

    // ---------- 一层层往下挂 ----------
    //
    // 界面上只有一个输入框。默认把新条目挂在大任务这一层；
    // 点某个阶段行的「往里加」，就把这条挂到那个阶段下面，
    // 于是「大任务 → 第 1 章 → 基本知识 / 做题 / 真题」不用第二个入口。
    // 容器只是 ParentTaskId 指向的一条普通记录，不是另一张表、也不是新类型。

    /// <summary>当前要挂到哪个阶段下；null = 挂在大任务顶层。</summary>
    [ObservableProperty]
    public partial Guid? ParentStageId { get; set; }

    /// <summary>当前父阶段的标题，只用于输入框提示语。</summary>
    [ObservableProperty]
    public partial string ParentStageTitle { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsAddingStage { get; set; }

    public bool HasParentStage => ParentStageId is not null;

    public string AddPlaceholder => IsAddingStage
        ? (HasParentStage ? $"往「{ParentStageTitle}」里加一个阶段……" : "给这一层起个名字……")
        : (HasParentStage ? $"往「{ParentStageTitle}」里加一条……" : "加一条子任务，回车就能加上");

    /// <summary>
    /// 挂靠提示条那句话。
    ///
    /// 【为什么在 C# 里拼好，不用 XAML 的 StringFormat】
    /// StringFormat='...{0}...' 里的花括号在 markup extension 里是特殊字符，
    /// 而这项目被「XAML 写错 = 编译 0 错 0 警、运行时整页炸」坑过一次（TimePage）。
    /// 一句话宁可在 VM 里算好，也不往 XAML 里塞可能被误解析的语法。
    /// </summary>
    public string ParentStageLabel => $"往「{ParentStageTitle}」里加";

    /// <summary>形态按钮上的字。只用 ASCII，别用字体里可能缺字形的箭头。</summary>
    public string AddKindText => IsAddingStage ? "加阶段 v" : "加任务 v";

    private void NotifyAddUi()
    {
        OnPropertyChanged(nameof(HasParentStage));
        OnPropertyChanged(nameof(AddPlaceholder));
        OnPropertyChanged(nameof(ParentStageLabel));
        OnPropertyChanged(nameof(AddKindText));
    }

    partial void OnParentStageIdChanged(Guid? value) => NotifyAddUi();
    partial void OnParentStageTitleChanged(string value) => NotifyAddUi();
    partial void OnIsAddingStageChanged(bool value) => NotifyAddUi();

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

            // 平铺的记录在这里组装成带层级的行：缩进表达层级，
            // 容器行只显示下级进度、没有完成按钮。组装是 Core 的纯函数，
            // 界面不自己再推一遍「谁挂在谁下面」。
            var rows = ProjectTree.Flatten(items);

            Items.Clear();
            foreach (var row in rows)
            {
                var vm = new ItemViewModel(row.Item)
                {
                    Depth = row.Depth,
                    IsContainer = ItemAxes.IsContainer(row.Item)
                };
                Items.Add(vm);
            }

            IsEmpty = Items.Count == 0;
            RefreshStageProgress();
            UpdateProgress();

            // 项目名单独取一次：GetItemsAsync 只给了任务，没带项目实体。
            var summary = (await _projects.GetActiveWithProgressAsync())
                .FirstOrDefault(x => x.Id == _projectId);

            if (summary is not null)
            {
                Name = summary.Name;
                TargetDate = summary.TargetDate?.ToLocalTime().DateTime;
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
            // 跟日程页走同一个方法（ToggleDoneAsync）：
            // 勾完成的那一刻要把这段计时结算进 ActualMinutes，
            // 否则大任务页里做完的事就没有实际用时，AI 排程还是照你的乐观估计来。
            var updated = await _items.ToggleDoneAsync(item.Id);

            if (updated is not null)
            {
                item.Update(updated);
                RefreshStageProgress();
                UpdateProgress();
            }
        }
        catch (Exception ex)
        {
            await ReportErrorAsync(ex);
        }
    }

    /// <summary>
    /// 启动 / 暂停。
    ///
    /// 【为什么详情页也要有这个按钮】
    /// 在这儿看大任务进度的人，下一步往往就是"开工做一条"。
    /// 还要切回日程页才能开始的话，这一趟切换本身就是拖延的温床。
    /// 命令的实现跟日程页指向同一个仓库方法，行为必然一致。
    /// </summary>
    [RelayCommand]
    private async Task ToggleStartPauseAsync(ItemViewModel item)
    {
        try
        {
            var updated = await _items.ToggleStartPauseAsync(item.Id);

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
            if (IsAddingStage)
            {
                // 阶段性目标是容器：没有完成按钮、不进今日清单、进度由下级汇总。
                // 复用数据层现成入口，界面不碰 Shape / CompletionMode。
                await _items.AddContainerAsync(NewTitle.Trim(),
                    parentTaskId: ParentStageId, projectId: _projectId);
            }
            else
            {
                // 叶子仍是「挂在今天的一条待办」，只是多了一个 ParentTaskId。
                // 不能走 AddToDayAsync：那个入口把 projectId 写死成 null，
                // 新加的叶子就不属于这个项目，GetItemsAsync 按 ProjectId 查不到它。
                // 这里显式传 shape，形态仍由入口写死，不靠时间字段反推。
                await _items.AddAsync(NewTitle.Trim(), projectId: _projectId,
                    parentTaskId: ParentStageId, shape: ItemShape.DayTask);
            }

            // 不清输入框内容的理由和日程页一致：加完之后通常还要接着加下一条。
            // 挂靠的阶段也留着：连着往同一章里加三条，不该每加一条重选一次。
            NewTitle = string.Empty;

            await LoadAsync();
            WeakReferenceMessenger.Default.Send(new ItemsChangedMessage());
        }
        catch (Exception ex)
        {
            await ReportErrorAsync(ex);
        }
    }

    /// <summary>输入框左边那个小按钮：在「加任务 / 加阶段」之间切。</summary>
    [RelayCommand]
    private void ToggleAddKind() => IsAddingStage = !IsAddingStage;

    /// <summary>
    /// 点某个阶段行的「往里加」：把它设成当前父阶段。
    /// 只有容器行会显示这个按钮（IsContainer），叶子行点不到。
    /// </summary>
    [RelayCommand]
    private void PickParentStage(ItemViewModel item)
    {
        if (item is null || !item.IsContainer)
        {
            return;
        }

        ParentStageId = item.Id;
        ParentStageTitle = item.Title;
    }

    /// <summary>回到顶层：清空挂靠的阶段。</summary>
    [RelayCommand]
    private void ClearParentStage()
    {
        ParentStageId = null;
        ParentStageTitle = string.Empty;
    }

    // ================= 改目标日期 =================

    /// <summary>
    /// 日期面板要不要显示。
    ///
    /// 【为什么这一页也要这个入口】
    /// 原来只有列表页的「更多」里能改，于是想调一天期限得先返回、找到那张卡片、
    /// 再点两层菜单。而人真正会想改日期的时刻，往往就是打开这一页看见
    /// "还剩 2 天、底下还有 7 条没做"的那一刻——入口不在这里，等于没有。
    ///
    /// 【为什么面板本身不用再写一遍】
    /// 用的是 <see cref="Controls.DatePickerSheet"/>，和列表页同一个控件。
    /// 各写一遍的话，以后调样式、改措辞要记得改两处，而漏掉一处的表现是
    /// 同一个动作在两个页面长得不一样——不报错，只是不像一个软件。
    /// </summary>
    [ObservableProperty]
    public partial bool IsDatePanelVisible { get; set; }

    /// <summary>日历上当前选中的那天。初值在 <see cref="OpenDatePanel"/> 里按原目标日期算。</summary>
    [ObservableProperty]
    public partial DateTime EditingDate { get; set; } = DateTime.Today.AddDays(1);

    /// <summary>日历能往前翻到哪。</summary>
    public DateTime EarliestDate => DateTime.Today;

    /// <summary>
    /// 打开面板。
    ///
    /// 【为什么初值取"原目标日期"和"明天"里较晚的那个】
    /// 已经有目标的：停在原来那天，改的时候是在原基础上微调。
    /// 没有目标、或者原目标已经过期的：从明天开始——
    /// 一个已经过期的日子还选在日历上，看着像"你上次定的就是这个"。
    /// </summary>
    [RelayCommand]
    private void OpenDatePanel()
    {
        var today = DateTime.Today;
        var existing = TargetDate?.Date;

        EditingDate = existing is null || existing.Value < today
            ? today.AddDays(1)
            : existing.Value;

        IsDatePanelVisible = true;
    }

    /// <summary>把日历上选的那天存下去。</summary>
    [RelayCommand]
    private async Task ConfirmDateAsync()
    {
        if (_projectId == Guid.Empty)
        {
            return;
        }

        IsDatePanelVisible = false;

        try
        {
            await _projects.SetTargetDateAsync(_projectId, DateOnly.FromDateTime(EditingDate));

            // 重查一次而不是就地改 TargetText：那句"还剩几天"是由 summary 算出来的，
            // 在内存里再算一遍等于把同一套算法抄第二份。
            await LoadAsync();

            // 列表页卡片上的"还剩 X 天"也要跟着变。
            // 自己也会收到这条消息、再查一次，代价是一次多余的读——数据量是一个人用的，不为它加排除逻辑。
            WeakReferenceMessenger.Default.Send(new ItemsChangedMessage());
        }
        catch (Exception ex)
        {
            await ReportErrorAsync(ex);
        }
    }

    /// <summary>清掉目标日期（还没想好什么时候做完）。</summary>
    [RelayCommand]
    private async Task ClearDateAsync()
    {
        if (_projectId == Guid.Empty)
        {
            return;
        }

        IsDatePanelVisible = false;

        try
        {
            await _projects.SetTargetDateAsync(_projectId, null);
            await LoadAsync();
            WeakReferenceMessenger.Default.Send(new ItemsChangedMessage());
        }
        catch (Exception ex)
        {
            await ReportErrorAsync(ex);
        }
    }

    /// <summary>快捷日期：3 天后 / 一周后 / 两周后 / 一个月后。</summary>
    [RelayCommand]
    private void PickQuickDate(string code)
    {
        var today = DateTime.Today;

        EditingDate = code switch
        {
            "3d" => today.AddDays(3),
            "1w" => today.AddDays(7),
            "2w" => today.AddDays(14),
            "1m" => today.AddMonths(1),
            _ => EditingDate
        };
    }

    /// <summary>什么都不改，收起面板。</summary>
    [RelayCommand]
    private void CancelDate() => IsDatePanelVisible = false;

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

    /// <summary>
    /// 容器行那句「1 / 3 件完成」就地重算，不重查数据库。
    /// 打勾只改一行，滚动位置不该跳；但容器进度在别的行上，
    /// 所以拿当前内存里的记录再算一次，两全。
    /// </summary>
    private void RefreshStageProgress()
    {
        var progress = ProjectTree.StageProgressOf(Items.Select(x => x.Item).ToList());
        foreach (var vm in Items)
        {
            if (!vm.IsContainer)
            {
                continue;
            }

            var (done, total) = progress.GetValueOrDefault(vm.Id);
            vm.StageProgressText = total == 0
                ? "还没有子任务"
                : $"{done} / {total} 件完成";
        }
    }

    private void UpdateProgress()
    {
        //
        // 口径和列表页保持一致：只数叶子、放弃的不算分母。
        // 容器自己没有完成按钮，把它算进分母会让同一件事算两次（父一条 + 子若干条），
        // 进度条永远走不到 100%。两处算法不一样的话，
        // 同一个项目在列表页显示 60%、进来显示 75%，用户只会觉得这软件算不明白。
        //
        var leaves = Items.Where(x => x.IsLeaf).ToList();
        var active = leaves.Count(x => x.Item.Status != ItemStatus.Abandoned);
        var done = leaves.Count(x => x.IsDone);

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
