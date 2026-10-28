using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using DayManager.App.Messages;
using DayManager.App.Services;
using DayManager.Data.Repositories;

namespace DayManager.App.ViewModels;

/// <summary>
/// 大任务列表页的大脑。
///
/// 【这一页和日程页的分工】
/// 日程页回答"今天做什么"，这一页回答"那件大事做到哪了"。
/// 两者缺一不可：只看日程页，你会觉得日子是一串零散的条目，
/// 做完三条也不知道那件事到底推进了多少；
/// 而"进展"正是对抗"高估自己 → 完不成 → 挫败"这条链路的关键正反馈。
///
/// 【为什么项目自己没有"完成"按钮】
/// 需求 2.4 定的：大任务只是容器，进度由子任务自动汇总。
/// 两层都能打勾必然矛盾——底下八条只做了三条，上面却能标完成，
/// 那这个勾是假的，而假的正反馈比没有反馈更伤。
/// 项目只有一个出口：归档（它的子任务全都了结之后）。
/// </summary>
public partial class ProjectsViewModel : ObservableObject
{
    private readonly ProjectRepository _projects;

    public ProjectsViewModel(ProjectRepository projects)
    {
        _projects = projects;

        // 和日程页同一个道理：AI 在对话里改了数据，这一页必须立刻跟着变。
        // 不能指望切 Tab 触发 OnAppearing——Shell 不保证。
        WeakReferenceMessenger.Default.Register<ItemsChangedMessage>(
            this,
            (_, _) => _ = LoadCommand.ExecuteAsync(null));

        // 换主题后重读：进度条、日期提示的颜色是在 ViewModel 里算的，不重算就还是旧值。
        WeakReferenceMessenger.Default.Register<ThemeChangedMessage>(
            this,
            (_, _) => _ = LoadCommand.ExecuteAsync(null));
    }

    public ObservableCollection<ProjectCardViewModel> Projects { get; } = new();

    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    [ObservableProperty]
    public partial bool IsEmpty { get; set; } = true;

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
            var summaries = await _projects.GetActiveWithProgressAsync();

            Projects.Clear();
            foreach (var s in summaries)
            {
                Projects.Add(new ProjectCardViewModel(s));
            }

            IsEmpty = Projects.Count == 0;
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
    /// 点开一个大任务，看它下面的子任务。
    ///
    /// 【为什么用相对路由，而且不带斜杠】
    /// 详情页在 AppShell 里用 Routing.RegisterRoute 注册成普通页面，
    /// 相对路由会把它**压进当前 Tab 的导航栈**：顶部出现系统返回箭头，
    /// 底部 Tab 还在，点「日程」也能直接走。
    /// 试过把它写成 XAML 里的 ShellContent，那就只能用 /// 导航，
    /// 而 /// 的意思是"替换整个 Shell 内容"——Tab 栏和返回按钮一起没了，
    /// 进去就出不来。这个坑的代价是把自己锁在详情页里。
    /// </summary>
    [RelayCommand]
    private async Task OpenAsync(ProjectCardViewModel card)
    {
        try
        {
            await Shell.Current.GoToAsync($"ProjectDetailPage?projectId={card.Id}");
        }
        catch (Exception ex)
        {
            await ReportErrorAsync(ex);
        }
    }

    /// <summary>
    /// 手动新建一个大任务。
    ///
    /// 【为什么要留这个入口】
    /// AI 不可用时（离线、key 过期、后端没起），至少还能自己开一个容器。
    /// 一个"只有 AI 会用"的功能，在 AI 挂掉的那一刻价值归零。
    /// 这里只问名字——截止日可以以后再补，一上来问两个问题会让人干脆不建。
    /// </summary>
    [RelayCommand]
    private async Task CreateAsync()
    {
        var page = CurrentPage;
        if (page is null)
        {
            return;
        }

        var name = await page.DisplayPromptAsync(
            "新建大任务", "这件事叫什么？", "创建", "取消",
            placeholder: "比如：数据结构期末复习");

        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        try
        {
            await _projects.CreateAsync(name.Trim(), null);
            await LoadAsync();
        }
        catch (Exception ex)
        {
            await ReportErrorAsync(ex);
        }
    }

    /// <summary>这一页上项目的手动操作：重命名 / 改截止日 / 归档。</summary>
    [RelayCommand]
    private async Task ShowActionsAsync(ProjectCardViewModel card)
    {
        var page = CurrentPage;
        if (page is null)
        {
            return;
        }

        var action = await page.DisplayActionSheetAsync(
            card.Name, "取消", null, "重命名", "改目标日期", "归档（不再显示）");

        try
        {
            switch (action)
            {
                case "重命名":
                    var newName = await page.DisplayPromptAsync(
                        "重命名", "改成：", "好", "取消", initialValue: card.Name);

                    if (!string.IsNullOrWhiteSpace(newName))
                    {
                        await _projects.RenameAsync(card.Id, newName.Trim());
                        await LoadAsync();
                    }

                    break;

                case "改目标日期":
                    // 打开页面上那个日历面板（见 IsDatePanelVisible）。
                    // 这里不能用 await 等它选完——面板是常驻的，由另两个命令收尾。
                    ShowDatePicker(card);
                    break;

                case "归档（不再显示）":
                    // 归档是这一页上唯一"让它消失"的操作，多问一句。
                    // 措辞里写明"不再显示"而不是"删除"，因为它确实没删——
                    // 说成删除，用户会以为底下的任务也没了。
                    var ok = await page.DisplayAlertAsync(
                        "归档", $"「{card.Name}」不再显示在这个列表里。底下的任务都还在。", "归档", "再想想");

                    if (ok)
                    {
                        await _projects.ArchiveAsync(card.Id);
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

    // ================= 选目标日期 =================

    /// <summary>
    /// 日历面板要不要显示。
    ///
    /// 【为什么做成页面上浮出来的一层，而不是继续用文字输入框】
    /// 最早这里是让人手打"2026-11-20"或"11月20日"再解析，
    /// 理由是"MAUI 没有跨平台日期弹窗"。但 DatePicker 一直都在（日程页顶部就用它），
    /// 所谓没有弹窗，指的是没有`await DisplaySomething()` 这样一行就能弹的东西——
    /// 日历控件本身从来就有。
    /// 于是打成 HTTP 的成本被甩给了用户：他要记住格式、可能打错、打错了还得看一句"没认出来"。
    /// 而"什么时候做完"这件事的正确交互就是看着日历点一下——
    /// 它不是一个要凭空想出来的字符串，是在一格里挑一个格子。
    ///
    /// 【为什么是浮层而不是跳到一个新页面】
    /// 定了日期之后人要看的是"现在还剩几天"，那张卡片就在浮层底下。
    /// 跳走再回来，中间多一次导航和一次"我刚才在看哪个来着"。
    /// </summary>
    [ObservableProperty]
    public partial bool IsDatePanelVisible { get; set; }

    /// <summary>面板标题：正在给哪个项目改日期。写清楚它，避免改错对象。</summary>
    [ObservableProperty]
    public partial string EditingProjectName { get; set; } = string.Empty;

    /// <summary>
    /// 日历上当前选中的那天。
    ///
    /// 【为什么初值取"原目标日期"和"明天"里较晚的那个】
    /// 已经有目标的：选中那天，改的时候是在原基础上微调，而不是从头挑一次。
    /// 没有目标、或者原目标已经过期的：从明天开始——
    /// 一个已经过期的日期还在日历上被选中，看起来像"你上次定的就是这个"。
    /// </summary>
    [ObservableProperty]
    public partial DateTime EditingDate { get; set; } = DateTime.Today.AddDays(1);

    /// <summary>日历能往前翻到哪。目标日期选到过去没有意义。</summary>
    public DateTime EarliestDate => DateTime.Today;

    /// <summary>正在改日期的那张卡片。命令之间靠它记住改的是谁。</summary>
    private ProjectCardViewModel? _editingCard;

    /// <summary>
    /// 打开面板。
    ///
    /// 【为什么这个是 void 而不是 async】
    /// 面板打开后要一直待着等用户点日期、点确定，那是另外两个命令的事。
    /// 这里 await 不到什么——它会一路 await 到天荒地老，把整条命令链挂住。
    /// </summary>
    private void ShowDatePicker(ProjectCardViewModel card)
    {
        _editingCard = card;
        EditingProjectName = card.Name;

        var today = DateTime.Today;
        var existing = card.TargetDate?.Date;

        EditingDate = existing is null || existing.Value < today
            ? today.AddDays(1)
            : existing.Value;

        IsDatePanelVisible = true;
    }

    /// <summary>把日历上选的那天存下去。</summary>
    [RelayCommand]
    private async Task ConfirmDateAsync()
    {
        if (_editingCard is null)
        {
            IsDatePanelVisible = false;
            return;
        }

        var card = _editingCard;
        IsDatePanelVisible = false;
        _editingCard = null;

        try
        {
            await _projects.SetTargetDateAsync(card.Id, DateOnly.FromDateTime(EditingDate));
            await LoadAsync();

            // 详情页也显示"还剩几天"，它注册了这条消息会自己重查。
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
        if (_editingCard is null)
        {
            IsDatePanelVisible = false;
            return;
        }

        var card = _editingCard;
        IsDatePanelVisible = false;
        _editingCard = null;

        try
        {
            await _projects.SetTargetDateAsync(card.Id, null);
            await LoadAsync();
            WeakReferenceMessenger.Default.Send(new ItemsChangedMessage());
        }
        catch (Exception ex)
        {
            await ReportErrorAsync(ex);
        }
    }

    /// <summary>
    /// 快捷选择：三天后 / 一周后 / 两周后 / 一个月后。
    ///
    /// 【为什么还要这几个按钮，日历不是点两下就行吗】
    /// 翻日历找"两周后的那一天"要点四五次、还要数格子；
    /// 而"两周内做完"恰恰是最常见的说法。
    /// 这几个按钮把最常用的几个答案摆在第一屏，日历留给"我心里有个具体日子"的时候。
    /// </summary>
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
    private void CancelDate()
    {
        IsDatePanelVisible = false;
        _editingCard = null;
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

/// <summary>
/// 列表里的一张项目卡片。
///
/// 【为什么要有这一层，不直接把 ProjectSummary 绑到界面上】
/// 界面要的是"还剩 12 天"这种话，数据库给的是 TargetDate 这个瞬间。
/// 这个翻译放在 ViewModel 里，XAML 就只管显示，不用在绑定里做日期运算。
/// </summary>
public partial class ProjectCardViewModel : ObservableObject
{
    public ProjectCardViewModel(Data.Repositories.ProjectSummary summary)
    {
        Id = summary.Id;
        Name = summary.Name;

        TargetDate = summary.TargetDate?.ToLocalTime().DateTime;

        var done = summary.DoneCount;
        var active = summary.ActiveCount;

        // 除零保护：一个刚建好还没拆任务的项目，active 是 0，
        // 不判的话进度条会显示 NaN，界面上表现为一条诡异的满格。
        ProgressRatio = active == 0 ? 0 : (double)done / active;
        ProgressText = active == 0
            ? "还没有任务"
            : $"{done} / {active} 件完成"
              + (summary.AbandonedCount > 0 ? $"（放弃 {summary.AbandonedCount} 件）" : string.Empty);

        HasProgress = active > 0;

        (TargetText, TargetColor) = BuildTargetText(summary.TargetDate);
        NextText = BuildNextText(summary.NextDueAt);
    }

    public Guid Id { get; }

    public string Name { get; }

    /// <summary>
    /// 原来的目标日期（本地时间）。
    ///
    /// 【为什么卡片要额外留一份原始日期】
    /// 改日期的时候日历要**从原来那天开始选**，而不是每次都从今天开始：
    /// 微调一个已经定好的日子，比重新挑一次省事得多。
    /// 展示用 TargetText（"还剩 12 天"）已经丢掉了这个信息，所以这里留一份原始值。
    /// </summary>
    public DateTime? TargetDate { get; }

    public double ProgressRatio { get; }

    public string ProgressText { get; }

    public bool HasProgress { get; }

    public string TargetText { get; }

    public string NextText { get; }

    public Color TargetColor { get; }

    /// <summary>
    /// 目标日期那一句人话。
    ///
    /// 【为什么必须换算成"还剩几天"】
    /// "11月20日"这个信息要自己在心里跟今天比一下才知道紧迫程度，
    /// 而人系统性地会低估这件事——"还有半个月呢"是最典型的错觉。
    /// 直接把天数写出来，省掉这次心算，也省掉算错的机会。
    /// </summary>
    private static (string Text, Color Color) BuildTargetText(DateTimeOffset? targetDate)
    {
        if (targetDate is null)
        {
            return ("还没定什么时候做完", AppColors.Muted);
        }

        var today = DateOnly.FromDateTime(DateTime.Now);
        var target = DateOnly.FromDateTime(targetDate.Value.ToLocalTime().DateTime);
        var days = target.DayNumber - today.DayNumber;

        return days switch
        {
            > 7 => ($"还剩 {days} 天", Color.FromArgb("#8A8A8A")),
            > 0 => ($"还剩 {days} 天", Color.FromArgb("#C07820")),
            0 => ("今天就是最后一天", Color.FromArgb("#C07820")),
            _ => ($"已过期 {-days} 天", Color.FromArgb("#CC7000"))
        };
    }

    /// <summary>
    /// 下一件还没做的事什么时候到期。
    ///
    /// 【为什么这一行比"完成 60%"更重要】
    /// 百分比是一个抽象的总结，它不指向任何动作；
    /// "下一件 11月2日到期"才是一个能立刻判断"今天该不该动它"的信息。
    /// 打开大任务页的人要的是后者。
    /// </summary>
    private static string BuildNextText(DateTimeOffset? nextDueAt)
    {
        if (nextDueAt is null)
        {
            return "底下还没有待办";
        }

        var local = nextDueAt.Value.ToLocalTime();
        var today = DateOnly.FromDateTime(DateTime.Now);
        var dueDay = DateOnly.FromDateTime(local.DateTime);

        if (dueDay < today)
        {
            return $"下一件已逾期 {today.DayNumber - dueDay.DayNumber} 天";
        }

        return dueDay == today ? "下一件就在今天" : $"下一件 {dueDay:M月d日}";
    }
}
