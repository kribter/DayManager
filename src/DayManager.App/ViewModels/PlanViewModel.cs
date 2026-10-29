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
/// 「拆解」页面的大脑：把一件模糊的大事变成一份能照着做的每日计划。
///
/// 【流程刻意分成两步：先拆解看结果，再确认写入】
/// 一步到位（输入完直接写进数据库）看起来更省事，实际有两个问题：
///   1. AI 可能理解错了你的意思，直接写进去就是一堆垃圾数据，清理成本高；
///   2. 更重要的：看到一份排好的计划在眼前，和"系统背着你安排了一堆事"，
///      心理感受完全相反。前者是"我同意了这个计划"，后者是"我被安排了"。
///      这个软件的全部意义是帮你执行，不是替你决定。
/// 所以中间必须有一个"你看一眼、点头"的环节。
/// </summary>
public partial class PlanViewModel : ObservableObject
{
    private readonly TaskDecomposer _decomposer;
    private readonly ProjectRepository _projects;
    private readonly ApiClient _api;
    private readonly BackendGuard _guard;
    private readonly AiSettings _ai;

    private SpreadingResult? _spread;

    /// <summary>这一次拆解的总预算（每天额度 × 可用天数）。</summary>
    private int _budgetMinutes;

    public PlanViewModel(
        TaskDecomposer decomposer,
        ProjectRepository projects,
        ApiClient api,
        BackendGuard guard,
        AiSettings ai)
    {
        _decomposer = decomposer;
        _projects = projects;
        _api = api;
        _guard = guard;
        _ai = ai;

        Deadline = DateTime.Today.AddDays(7);
        DailyMinutes = PlanSpreader.DefaultDailyMinutes;
        StatusText = "还没开始";
    }

    // ================= 输入 =================

    [ObservableProperty]
    public partial string RawInput { get; set; } = string.Empty;

    /// <summary>
    /// 截止日期。默认给一周后——给一个具体的默认值比留空好，
    /// 空着的日期框容易让人卡住："我也不知道什么时候要做完"。
    /// </summary>
    [ObservableProperty]
    public partial DateTime Deadline { get; set; }

    /// <summary>
    /// 这件事每天最多给它多少分钟。
    ///
    /// 【口径：是"给这一件事的上限"，不是一天的总容量】
    /// 当天还排着别的事时，那些事不占这里的额度。
    /// 两件事各占两小时，不等于他一天只有两小时——
    /// 全天是否过载由 AI 看着当天已排的内容来判断，不靠这个数去限。
    ///
    /// 【为什么这个值要摆在界面上让用户看见和能改】
    /// 它直接决定排出来的是"轻松完成"还是"又一次安排过多"。
    /// 藏起来的话，用户排不下时只会觉得"软件不行"，
    /// 而看不到真正的原因——是自己设定的投入量 unrealistic。
    /// 默认值偏保守，见 PlanSpreader.DefaultDailyMinutes 的说明。
    /// </summary>
    [ObservableProperty]
    public partial int DailyMinutes { get; set; }

    /// <summary>
    /// 排的第一天是不是今天。
    ///
    /// 【为什么默认改成 true——这是修一个真实 bug，不是一个选项】
    /// 第一版固定从明天开始排，理由是"今天通常已经被占满了"。
    /// 但实测结果是：用户确认写入后切回今日清单，一条都没有，
    /// 第一反应是"写进去了吗？是不是坏了"。没有任何地方告诉他计划从明天才开始。
    ///
    /// 更重要的是产品上的判断：刚规划完的那一刻，是启动意愿最强的时刻。
    /// 把第一条任务放到今天，等于把这个意愿接住；
    /// 推到明天，等于给了自己一晚上时间反悔。
    /// 这个软件的核心主张就是降低启动摩擦，推迟开始从根上违背它。
    ///
    /// 担心"今天被塞满"其实不用担心：PlanSpreader 有每日额度上限，
    /// 超出部分会自动顺延到后面几天，而不会堆在今天。
    ///
    /// 仍然保留"从明天开始"的开关，是因为确实会有"今晚不打算干活"的时候——
    /// 关键是这个选择要用户自己看见，而不是代码替他决定。
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MinDate))]
    public partial bool StartsToday { get; set; } = true;

    /// <summary>数据在用的每日额度（界面上可能填了 0 或负数）。</summary>
    private int EffectiveDailyMinutes =>
        DailyMinutes > 0 ? DailyMinutes : PlanSpreader.DefaultDailyMinutes;

    /// <summary>
    /// 计划的第一天。
    ///
    /// 【为什么上限今天的午夜，而不是 DateTime.Today】
    /// 两个用户真正的差别在于"今天算不算"，这里已经由 StartsToday 表达了。
    /// 并不存在"从三天后开始"的合理场景——真要推迟，改的就是截止日。
    /// </summary>
    private DateOnly StartDate =>
        StartsToday
            ? DateOnly.FromDateTime(DateTime.Today)
            : DateOnly.FromDateTime(DateTime.Today.AddDays(1));

    /// <summary>
    /// 是否正在忙。
    ///
    /// 【为什么还要额外写 IsNotBusy / HasNoPlan 这种取反的属性】
    /// XAML 里没有内置的"取反"写法，要用就得引一个转换器包
    /// （CommunityToolkit.Maui 才有，本项目只引了 Mvvm 那一套）。
    /// 为一个取反去加一整个依赖不划算——
    /// 而且转换器的报错发生在运行时，拼错了界面只是不显示，很难查。
    /// 在这里多写两个属性，换來的是编译期检查和零依赖。
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsNotBusy))]
    public partial bool IsBusy { get; set; }

    public bool IsNotBusy => !IsBusy;

    [ObservableProperty]
    public partial string StatusText { get; set; } = string.Empty;

    // ================= 拆解结果 =================

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasUnderstanding))]
    public partial string Understanding { get; set; } = string.Empty;

    public bool HasUnderstanding => !string.IsNullOrWhiteSpace(Understanding);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNoPlan))]
    public partial bool HasPlan { get; set; }

    public bool HasNoPlan => !HasPlan;

    [ObservableProperty]
    public partial bool IsServerReady { get; set; }

    /// <summary>日期选择器能选的最早日期。</summary>
    public DateTime MinDate => StartsToday ? DateTime.Today : DateTime.Today.AddDays(1);

    public ObservableCollection<string> Warnings { get; } = new();

    /// <summary>
    /// AI 缺的关键信息，每条已经在输入框里预填了它的猜测。
    ///
    /// 【为什么是"能回答"的列表，而不是像 warnings 那样只能看】
    /// warnings 是 AI 的异议，听完决定了就行；
    /// questions 是它真的不知道、而不知道就拆不准的东西——必须给个回填的出口，
    /// 否则用户只能自己回输入框把原始描述改长，但他并不知道该补什么。
    /// </summary>
    public ObservableCollection<QuestionViewModel> Questions { get; } = new();

    [ObservableProperty]
    public partial bool HasQuestions { get; set; }

    /// <summary>
    /// 这份计划超出了"每天 X 分钟 × N 天"的预算。
    ///
    /// 【为什么要把它做成界面上的一个状态，而不是只在文字里提一句】
    /// 超预算时用户需要一个动作入口（砍到预算内），而不只是一句提醒。
    /// 提醒只能让人知道"不对"，按钮才能让人"改"。
    /// </summary>
    [ObservableProperty]
    public partial bool IsOverBudget { get; set; }

    /// <summary>按天分组后的计划，界面直接照着画。</summary>
    public ObservableCollection<PlanDayViewModel> Days { get; } = new();

    // ================= 命令 =================

    /// <summary>
    /// 页面出现时先探一下服务器，后端没在跑就自己把它拉起来。
    ///
    /// 【为什么要在打开页面时就查，而不是等点按钮】
    /// 用户可能已经把一整段任务描述打完了才点"拆解"，
    /// 那时才发现服务器没开，等于白输入一遍。早一秒知道，少一次挫败。
    ///
    /// 【为什么连不上时不直接报错，而是先试着自己启动】
    /// "连不上服务器"这个提示会让人以为是程序坏了。
    /// 但后端是同一个软件的一部分，用户不该需要知道它存在，
    /// 更不该需要记得先去开它。能自己解决的事就不要抛给他。
    /// </summary>
    [RelayCommand]
    private async Task CheckServerAsync()
    {
        // 探活 + 必要时自己拉起后端 + 等它能响应，这套逻辑两个页面都要用，
        // 已经抽到 BackendGuard 里了。复制两份的后果是以后改一处忘一处，
        // 表现为"对话页能自动拉起后端、拆解页不行"这种莫名其妙的差异。
        var health = await _guard.EnsureAsync(status => StatusText = status);
        ApplyHealth(health);
    }

    private void ApplyHealth(HealthResult health)
    {
        // 密钥改成界面填之后，服务端配置里那把是刻意留空的；
        // 时间页填的那把会随每次请求带上，同样算就绪。
        var aiUsable = health.AiReady || _ai.HasKey;
        IsServerReady = health.Ok && aiUsable;

        StatusText = health.Ok
            ? aiUsable
                ? health.AiReady
                    ? $"已连上后端，AI 就绪（{health.Provider} / {health.Model}）"
                    : "已连上后端，AI 就绪（用你在时间页填的密钥）"
                : "后端在线，但 AI 密钥还没填。到时间页的「AI 接入」里填一下。"
            : $"连不上后端：{health.ErrorMessage}";
    }

    /// <summary>
    /// 让 AI 拆一下。
    ///
    /// 【为什么拆完要跳到一个专门的页面去看结果】
    /// 结果本身挺长：AI 的理解、它缺的信息、每天的安排、每条的估时。
    /// 这些东西和输入框挤在同一屏，两边都得被压扁——
    /// 输入框只剩三行看不见自己写了什么，结果要滚动才能看全。
    /// 分成"提问页"和"结果页"之后，两页各自都不拥挤：
    /// 输入的地方专心输入，看的地方专心看。
    /// 而且"看完的结果要么确认、要么返回"，恰好就是一个明确的决定点——
    /// 一个页面同时让人修改输入和做决定，是最容易让人犹豫的排布。
    /// </summary>
    [RelayCommand]
    private async Task DecomposeAsync()
    {
        await RunDecomposeAsync(openResult: true);
    }

    /// <summary>
    /// 用补充的信息再拆一次。只在结果页出现，因为只有那里才有要填的框。
    /// </summary>
    [RelayCommand]
    private async Task RedecomposeAsync()
    {
        await RunDecomposeAsync(openResult: false);
    }

    /// <summary>
    /// 让 AI 把这一份压到预算内。
    ///
    /// 【为什么这个按钮必须由软件主动给，而不是指望用户自己去改输入】
    /// 超预算这件事，用户能做的正确动作其实是"让 AI 重拆"，
    /// 但他并不知道该在输入框里补哪句话才能表达这个意思
    /// （写"少一点"太模糊，写"每条不超过 120 分钟"又要先搞懂机制）。
    /// 一个按钮把这句话替他说了，摩擦就消失了。
    ///
    /// 【为什么提示里要把具体数字写进去】
    /// 只说"压缩一下"，模型会自己决定砍到什么程度，多半砍得不够或者砍过头。
    /// 给出"总预算是多少、单条上限是多少"，它才有可能真的算着来。
    /// </summary>
    [RelayCommand]
    private async Task TrimToBudgetAsync()
    {
        var daily = EffectiveDailyMinutes;
        var instruction =
            $"上一次你拆出来的总时长超出了预算。这次必须满足："
            + $"所有子任务的 estimateMinutes 之和不超过 {_budgetMinutes} 分钟，"
            + $"并且任何一条不超过 {daily} 分钟。"
            + "做法是先砍掉相对不重要、可以以后再做或干脆不做的子任务"
            + "（在 warnings 里说明砍掉了哪些、为什么），"
            + "如果砍到只剩必要的还是超，就把剩下的每条再拆细、把估时压到更短。"
            + "注意：不要用低估的方式凑预算——宁可少排几条，也不要让排出来的时间做不完。";

        await RunDecomposeAsync(openResult: false, extraInstruction: instruction);
    }

    private async Task RunDecomposeAsync(bool openResult, string? extraInstruction = null)
    {
        if (string.IsNullOrWhiteSpace(RawInput))
        {
            StatusText = "先写下你要做的事。";
            return;
        }

        if (IsBusy)
        {
            return;
        }

        // 有回答就带着回答重拆，没回答就是第一轮。
        // 用户不需要理解这两轮的存在：第一轮之后如果 AI 还在问，
        // 结果页上会多出一个"再拆一次"的按钮，点它就是第二轮。
        var answers = Questions
            .Where(x => !string.IsNullOrWhiteSpace(x.Answer))
            .Select(x => new ClarifyingAnswer(x.Prompt, x.Answer))
            .ToList();

        IsBusy = true;
        HasPlan = false;
        IsOverBudget = false;
        Days.Clear();
        Warnings.Clear();
        Understanding = string.Empty;
        StatusText = answers.Count > 0
            ? "带着你补充的信息重新拆解……"
            : "AI 正在拆解，可能要等二三十秒……";

        try
        {
            var deadline = DateOnly.FromDateTime(Deadline);
            var start = StartDate;
            var daily = EffectiveDailyMinutes;

            // 可用天数要算给 AI 看，否则它不知道总预算是多少。
            // 至少算 1 天：截止日填成今天时，"0 天"会让预算变成 0，
            // 模型拿到一个不可能满足的约束，只会胡乱应付。
            var availableDays = Math.Max((deadline.DayNumber - start.DayNumber) + 1, 1);

            var result = await _decomposer.DecomposeAsync(
                RawInput,
                Deadline,
                answers.Count > 0 ? answers : null,
                dailyMinutes: daily,
                availableDays: availableDays,
                extraInstruction: extraInstruction);

            if (!result.Success)
            {
                StatusText = result.ErrorMessage ?? "拆解失败。";
                return;
            }

            Understanding = result.Understanding ?? string.Empty;
            foreach (var w in result.Warnings)
            {
                Warnings.Add(w);
            }

            // 用新的回答替换掉旧的问题列表。
            // 【为什么要清空旧的而不是保留】
            // 第二轮的答案已经融进新的理解里了，旧问题还挂着的话，
            // 用户会以为自己答的那几条没生效，还得再答一遍。
            Questions.Clear();
            foreach (var q in result.Questions)
            {
                Questions.Add(new QuestionViewModel(q));
            }

            HasQuestions = Questions.Count > 0;

            _spread = PlanSpreader.Spread(result.Subtasks, start, deadline, daily);
            _budgetMinutes = daily * availableDays;

            foreach (var group in _spread.Scheduled.GroupBy(x => x.Date))
            {
                // 把每天的额度一起传给这一行：它要靠它判断"今天超没超"，
                // 而不是只显示一个总数让人自己去比。
                var day = new PlanDayViewModel(group.Key, daily);
                var index = 1;

                foreach (var scheduled in group)
                {
                    day.Tasks.Add(new PlannedTaskViewModel(index++, scheduled.Task));
                }

                Days.Add(day);
            }

            HasPlan = true;
            StatusText = _spread.Describe();

            // 排不下时把"还剩几条没排"说清楚，而不是让用户以为都排好了。
            if (!_spread.Fits)
            {
                StatusText += " 上面的任务只排到了截止日，剩下的没有放进去。";
            }

            // 【为什么超预算要单独算一遍并给出行动建议】
            // 光说"有 2 天超了"是没用的：用户不知道该改什么。
            // 告诉他"是因为有单条就超过一天额度的任务"，他才知道该让 AI 拆细，
            // 而不是去把每天额度调高（调高只是让数字好看，做不完还是做不完）。
            var overDays = _spread.OverBudgetDays;
            IsOverBudget = overDays > 0 || _spread.TotalMinutes > _budgetMinutes;

            if (overDays > 0)
            {
                Warnings.Add($"有 {overDays} 天超过了每天 {daily} 分钟（最多的一天 "
                             + $"{_spread.MaxDayMinutes} 分钟）。原因是其中有些任务单条就超过一天的额度，"
                             + "程序不会把它们切碎到两天——那样反而更难启动。"
                             + $"可以点下面的「砍到预算内」让 AI 重拆，或者把截止日往后推。");
            }
            else if (_spread.TotalMinutes > _budgetMinutes)
            {
                Warnings.Add($"总共 {_spread.TotalMinutes} 分钟，超过了 {availableDays} 天 × 每天 {daily} 分钟"
                             + $" = {_budgetMinutes} 分钟的预算，后面排不下的部分没有放进去。"
                             + "可以点「砍到预算内」让 AI 压缩，或者把截止日往后推。");
            }

            // AI 提了问题时，明确告诉用户下一步可以做什么。
            // 不说的话，用户看不到"我应该在这儿填点东西"这条路径。
            if (HasQuestions)
            {
                StatusText += "  AI 有几个地方缺信息，填一下上面的框再拆会更准。";
            }

            // 结果好了，通知界面把它显示到结果页。
            // 第二轮（Redecompose）不发这条：那时结果页已经开着了，
            // 再开一次会叠出第二个相同的页面。
            if (openResult)
            {
                WeakReferenceMessenger.Default.Send(new PlanReadyMessage());
            }
        }
        catch (Exception ex)
        {
            StatusText = $"出错了：{ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// 确认写入。写完就把结果页关掉，回到「今天」或者上一级。
    ///
    /// 【为什么确认之后一定要回到上一页，而不是停在结果页】
    /// 停在这里的话，用户会不知道接下来干嘛——计划已经存好了，页面上却还是一堆任务，
    /// 手一滑再点一次还可能写进去第二遍。做完一件事就该离开那件事的画面。
    /// 回到「今天」页那边刚写好任务正好排着，"我刚安排的"和"现在该做什么"接上了。
    /// </summary>
    [RelayCommand]
    private async Task ConfirmAsync()
    {
        if (_spread is null || _spread.Scheduled.Count == 0)
        {
            return;
        }

        // 项目名直接用用户输入的第一行。
        // 【为什么不让用户再填一个项目名】
        // 多一个输入框就多一次决策，而这一步的摩擦会直接劝退。
        // 第一行通常就是这件事的核心描述，够用了，以后想改再改。
        var firstLine = RawInput
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault() ?? RawInput.Trim();

        var name = firstLine.Length > 30 ? firstLine[..30] + "…" : firstLine;

        try
        {
            await _projects.CreateWithTasksAsync(
                name,
                DateOnly.FromDateTime(Deadline),
                _spread.Scheduled);

            // 说清楚"写进去了、写在哪儿"，而不是笼统地说成功。
            // 用户刚才是从「今天」页切过来的，他想知道的第一件事是"今天有没有我的任务"。
            var today = DateOnly.FromDateTime(DateTime.Today);
            var todayCount = _spread.Scheduled.Count(x => x.Date == today);
            var firstDay = _spread.Scheduled[0].Date;

            StatusText = todayCount > 0
                ? $"已写入「{name}」，共 {_spread.Scheduled.Count} 条，其中今天 {todayCount} 条。"
                : $"已写入「{name}」，共 {_spread.Scheduled.Count} 条，第一条在 {firstDay:M月d日}。";

            // 告诉今日清单"数据变了，重新读一遍"。
            // 不通知的话，用户切回去还是空的，会以为没写进去——这正是第一版的 bug。
            WeakReferenceMessenger.Default.Send(new ItemsChangedMessage());

            // 清掉计划，避免用户以为还能再点一次确认、把同样的任务写两遍。
            HasPlan = false;
            IsOverBudget = false;
            _budgetMinutes = 0;
            Days.Clear();
            Warnings.Clear();
            Questions.Clear();
            HasQuestions = false;
            Understanding = string.Empty;
            RawInput = string.Empty;
            _spread = null;

            // 写入成功后关掉结果页。失败时不关——
            // 没写进去却把结果关掉了，用户会以为已经成功。
            WeakReferenceMessenger.Default.Send(new PlanClosedMessage());
        }
        catch (Exception ex)
        {
            StatusText = $"写入失败：{ex.Message}";
        }
    }

    /// <summary>
    /// 用户看完不打算要这份计划，返回上一级。
    ///
    /// 【为什么不清掉已经算好的计划】
    /// 返回不等于推翻。用户可能是想回去改一句原始描述再回来，
    /// 也可能只是想切到「今天」看一眼手上还有什么。
    /// 这时候把结果丢掉，等于强迫他重新拆解一遍（又要等二三十秒）。
    /// 所以计划留在内存里，拆解页上会出现"看上一次的结果"这个入口，随时能回去。
    /// </summary>
    [RelayCommand]
    private void CloseResult()
    {
        WeakReferenceMessenger.Default.Send(new PlanClosedMessage());
    }

    /// <summary>
    /// 回到结果页再看一眼（在拆解页上点"看拆解结果"时走这里）。
    /// </summary>
    [RelayCommand]
    private void OpenResult()
    {
        // 没有排好的计划就什么都不做。与其开一个空白页让人困惑，
        // 不如让按钮压根不出现（界面上也是这么处理的，这里是双保险）。
        if (HasPlan)
        {
            WeakReferenceMessenger.Default.Send(new PlanReadyMessage());
        }
    }
}

/// <summary>
/// 某一天要做的几条任务。
/// </summary>
public partial class PlanDayViewModel : ObservableObject
{
    private readonly DateOnly _date;
    private readonly int _budgetMinutes;

    public PlanDayViewModel(DateOnly date, int budgetMinutes = 0)
    {
        _date = date;
        _budgetMinutes = budgetMinutes;

        var culture = new CultureInfo("zh-CN");
        var weekday = date.ToString("dddd", culture);

        // 把"明天""后天"这种相对说法直接写出来。
        // 用户看日期时要自己换算"10-28 是周几、离今天还有几天"，
        // 多一次心算就多一点摩擦，不如直接说清楚。
        var daysFromToday = date.DayNumber - DateOnly.FromDateTime(DateTime.Today).DayNumber;
        var relative = daysFromToday switch
        {
            1 => "明天",
            2 => "后天",
            _ => $"{daysFromToday} 天后"
        };

        DateText = $"{date:MM月d日} {weekday}（{relative}）";
    }

    public ObservableCollection<PlannedTaskViewModel> Tasks { get; } = new();

    [ObservableProperty]
    public partial string DateText { get; set; } = string.Empty;

    /// <summary>这一天实际排了多少分钟。</summary>
    private int TotalMinutes => Tasks.Sum(x => x.EstimateMinutes);

    /// <summary>这一天超出了预算多少分钟（没超就是 0）。</summary>
    public int OverMinutes => _budgetMinutes > 0 ? Math.Max(TotalMinutes - _budgetMinutes, 0) : 0;

    /// <summary>
    /// 这一天总共要花多少时间。
    ///
    /// 【为什么超了要直接把"超了多少"写在旁边】
    /// 只显示总数的话，用户要自己拿它和"这件事每天 120 分钟"去比——
    /// 而这个比较恰恰是他没耐心做、也最容易忘的一步。
    /// 把差值写出来，"这天太满了"就变成一眼可见的事实。
    /// </summary>
    public string TotalText =>
        DurationText.About(TotalMinutes)
        + (OverMinutes > 0 ? $"，超 {DurationText.Format(OverMinutes)}" : string.Empty);

    /// <summary>超预算的那一天把数字标出来，让视线自动落在需要处理的那天上。</summary>
    public Color TotalColor => OverMinutes > 0 ? AppColors.Danger : AppColors.Muted;

    public DateOnly Date => _date;
}

/// <summary>
/// 待用户补充的一条关键信息。输入框里已经预填了 AI 的猜测。
/// </summary>
public partial class QuestionViewModel : ObservableObject
{
    public QuestionViewModel(ClarifyingQuestion question)
    {
        Prompt = question.Prompt;
        Answer = question.Guess;
    }

    /// <summary>AI 问的那句话，显示在输入框上方。</summary>
    [ObservableProperty]
    public partial string Prompt { get; set; } = string.Empty;

    /// <summary>
    /// 用户的回答，默认是 AI 的猜测。
    ///
    /// 【为什么预填而不是留空】
    /// 见 ClarifyingQuestion.Guess 的说明：让人改一句话，比让人想一句话容易得多。
    /// 留空的输入框对正在逃避的人来说本身就是一道坎。
    /// </summary>
    [ObservableProperty]
    public partial string Answer { get; set; } = string.Empty;
}

/// <summary>
/// 计划里的单条任务。
/// </summary>
public partial class PlannedTaskViewModel : ObservableObject
{
    public PlannedTaskViewModel(int order, PlannedTask task)
    {
        Order = order;
        Title = task.Title;
        EstimateMinutes = task.EstimateMinutes;
        EstimateText = DurationText.Format(task.EstimateMinutes);
        MinimalStep = task.MinimalStep;
        KindText = task.Kind == ItemKind.WantToDo ? "想做" : "该做";
        KindColor = task.Kind == ItemKind.WantToDo ? "#8A6D3B" : "#5A6B7B";
    }

    [ObservableProperty]
    public partial int Order { get; set; }

    [ObservableProperty]
    public partial string Title { get; set; } = string.Empty;

    [ObservableProperty]
    public partial int EstimateMinutes { get; set; }

    /// <summary>
    /// 这条预计花多久。
    ///
    /// 【为什么这一版要把它摆出来】
    /// 第一版只在"每天合计"里给了总数，单条没写。用户看不到每条任务的分量，
    /// 于是判断不了"今天这三条加起来到底多不多"，也就没法自己调整。
    /// 估时是 AI 拆解里唯一可量化的输出，藏起来等于白算。
    /// </summary>
    [ObservableProperty]
    public partial string EstimateText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string? MinimalStep { get; set; }

    /// <summary>AI 有没有给出最小启动步骤（没给就不显示那一行）。</summary>
    public bool HasMinimalStep => !string.IsNullOrWhiteSpace(MinimalStep);

    [ObservableProperty]
    public partial string KindText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string KindColor { get; set; } = string.Empty;
}
