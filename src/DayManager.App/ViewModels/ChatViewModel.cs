using System.Collections.ObjectModel;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using DayManager.App.Messages;
using DayManager.App.Services;
using DayManager.Core.Entities;
using DayManager.Core.Planning;
using DayManager.Data.Repositories;

namespace DayManager.App.ViewModels;

/// <summary>
/// 对话页的大脑。这是整个软件的核心界面。
///
/// 【这个页面和之前「拆解」页的根本区别】
/// 拆解页是一个表单：填一件事、点按钮、拿到一份计划。
/// 它能做的事从一开始就被限定成"拆任务"这一件。
/// 对话页没有这个限制——你随口说一句，AI 可以新建、改期、改状态、改名、删除，
/// 也可以什么都不改只是回你一句话。
/// 换句话说：**这里是唯一一个"什么都能操作"的地方**，另外三个页面是它的手和眼睛。
///
/// 【为什么改数据不用先确认】
/// 每改一条都弹一次"确定吗"，用起来像在填审批单，
/// 而正在逃避的人最受不了多一道手续。
/// 所以改成：直接改 → 说清楚改了哪几条 → 旁边给一个「撤回」。
/// 撤回的代价低，事前确认的代价高（它拦住的不只是错误，还有正常的动作）。
/// </summary>
public partial class ChatViewModel : ObservableObject
{
    /// <summary>
    /// 带去给模型的历史条数上限。
    ///
    /// 【为什么要截断】
    /// 对话是跨天延续的（决策 D9），不截断的话三个月后每一轮都要带上几千条历史，
    /// 既慢又贵，而且模型会被早期内容带偏。
    /// 真正的长期记忆不靠堆历史，靠每天结束时的摘要——那是后面要做的事。
    /// </summary>
    private const int HistoryLimit = 20;

    /// <summary>
    /// 打开页面时往回加载多少条。
    ///
    /// 【为什么比带给模型的条数多】
    /// 模型只看最近 20 条就够判断当前情况，但人是往上翻着看的——
    /// 他可能想确认"上次那件事说到哪了"。50 条大约是一周的量，
    /// 再多就该做分页了，现在还没有这个需求。
    /// </summary>
    private const int LoadLimit = 50;

    private readonly ApiClient _api;
    private readonly BackendGuard _guard;
    private readonly ChatRepository _chat;

    /// <summary>历史只加载一次。切 Tab 回来时 Shell 会再触发，重复加载会把列表翻倍。</summary>
    private bool _loaded;

    public ChatViewModel(ApiClient api, BackendGuard guard, ChatRepository chat)
    {
        _api = api;
        _guard = guard;
        _chat = chat;

        StatusText = "还没说话";

        // 「清空」按钮只在聊过之后出现，集合变了要通知一次，不然它永远不显示。
        Messages.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasMessages));

        // AI 主动说的话要能立刻看见。它是在后台线程上写进库的，
        // 不订阅就只有下次重新进这个页面时才出现——
        // 而"通知弹了、打开对话页却找不到那句话"是最让人怀疑自己记错的情况。
        WeakReferenceMessenger.Default.Register<ProactiveSpokeMessage>(
            this, (_, _) => _ = AppendLatestAsync());

        // 气泡的底色和文字色是每个气泡生成时算好的（BubbleColor / BubbleTextColor），
        // 换个主题它们不会自己变：新气泡是深色、旧气泡还是浅底黑字，
        // 一屏里两种配色。所以这里整页重读一遍。
        WeakReferenceMessenger.Default.Register<ThemeChangedMessage>(
            this, (_, _) => _ = ReloadForThemeAsync());
    }

    /// <summary>
    /// 把最新一条记录追加到界面上（AI 主动说话后调用）。
    ///
    /// 【为什么重新读库而不是把文字传过来】
    /// 消息里带上文字当然更省事，但那样这份显示逻辑就有了两条路：
    /// 一条从库里读、一条从消息里拿，两条路的字段迟早不一致（撤回状态就是个例子）。
    /// 重读一次库很便宜，口径只有一份。
    /// </summary>
    private async Task AppendLatestAsync()
    {
        // 历史还没加载过的话不用管：等打开页面时那一次加载自然会带上它。
        if (!_loaded)
        {
            return;
        }

        try
        {
            var records = await _chat.GetRecentAsync(1);

            if (records.Count == 0)
            {
                return;
            }

            var bubble = ChatBubble.FromRecord(records[0]);

            // 集合只能在 UI 线程上改——后台线程改 ObservableCollection
            // 抛的异常会被静默吞掉，表现是"界面一动不动"。
            await MainThread.InvokeOnMainThreadAsync(() =>
            {
                // 重复追加要防：消息可能因为重试到了两次。
                if (Messages.Count > 0 && Messages[^1].Text == bubble.Text)
                {
                    return;
                }

                Messages.Add(bubble);
            });
        }
        catch (Exception ex)
        {
            CrashLogger.Write("追加 AI 主动说的话", ex);
        }
    }

    /// <summary>有没有聊过。决定「清空」按钮要不要出现。</summary>
    public bool HasMessages => Messages.Count > 0;

    /// <summary>对话气泡。最新的在最后。</summary>
    public ObservableCollection<ChatBubble> Messages { get; } = new();

    /// <summary>
    /// AI 猜的、你接下来最可能说的几句。显示在输入框上方，点一下就发出去。
    ///
    /// 【为什么放在输入框上方这一条窄窄的位置】
    /// 它原先是输入框下方的一排预填输入框（questions），
    /// 实测的问题是那一片把 AI 的回复顶出了屏幕——问完一句想看它说什么，
    /// 结果先看见三行要填的框。话说被控件挤走了，这是最坏的一种排布。
    /// 挪到输入框上方：视线从"AI 说了什么"往下走到"我要说什么"的路上
    /// 正好经过它，既不挡回复，也正好落在手要按的地方。
    ///
    /// 【为什么存的是完整句子，而不是"填进输入框等他改"】
    /// 点一下就真的发出去。若只是填进输入框再等他按发送，
    /// 中间就多了一个"要不要发"的路口——而这个路口正是最容易拐去别处的地方。
    /// </summary>
    public ObservableCollection<string> Suggestions { get; } = new();

    [ObservableProperty]
    public partial string InputText { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSuggestions))]
    public partial bool IsBusy { get; set; }

    [ObservableProperty]
    public partial string StatusText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsServerReady { get; set; }

    /// <summary>
    /// 状态条上那个小圆点的颜色。
    ///
    /// 【为什么不写在 XAML 里用转换器】
    /// 写成 `{Binding IsServerReady, Converter=...}` 需要一个额外的转换器类，
    /// 而且绑定写错时 XAML 不会报错，只是颜色不对——很难查。
    /// 一个属性就能说清的事，就别多一个类。
    /// </summary>
    [ObservableProperty]
    public partial Color StatusColor { get; set; } = Colors.Gray;

    partial void OnIsServerReadyChanged(bool value) =>
        StatusColor = value ? AppColors.Done : AppColors.Muted;

    /// <summary>
    /// 有没有候选回复可显示。
    ///
    /// 【为什么 AI 在想的时候要收起来】
    /// 等回复的那二三十秒里，这一排按钮还是上一轮的猜测——
    /// 它描述的是"上一个状态的你可能说的话"，点下去答非所问。
    /// 留着不消失，用户会以为是 AI 已经回了、只是还显示着旧内容。
    /// </summary>
    public bool HasSuggestions => Suggestions.Count > 0 && !IsBusy;

    // ================= 历史 =================

    /// <summary>
    /// 打开对话页时把之前聊过的捞回来。
    ///
    /// 【为什么这一件事值得做】
    /// 对话是这个软件的主界面，而它原先只活在内存里：
    /// 关掉程序再打开，昨天说好的计划、AI 答应过的安排，一个字都不剩。
    /// 那不只是"看不到记录"——AI 每次都从零开始认识你，
    /// 于是它每天重新劝一遍同样的话，而"被重复劝"正是会让人不想打开这个软件的体验。
    /// 决策 D9（上下文跨天延续）能不能成立，全在这一次加载上。
    ///
    /// 【为什么失败要给提示而不是静默】
    /// 静默的后果是"我明明存了却看不到"，用户会以为功能没做。
    /// 存不进去这件事本身必须被看见。
    /// </summary>
    /// <summary>
    /// 换主题之后重新读一遍历史。
    ///
    /// 【为什么非得整页重读，不能只改颜色属性】
    /// 气泡的两个颜色都写在 ChatBubble 上，而气泡是数据行、
    /// 不是控件——没有一个"所有气泡都听着"的通知可以发。
    /// 重读一次库很便宜（最近 50 条），换来的是配色整齐。
    /// 历史还没加载过时不用管，等打开页面那一次自然会带上新配色。
    /// </summary>
    private async Task ReloadForThemeAsync()
    {
        if (!_loaded)
        {
            return;
        }

        _loaded = false;
        Messages.Clear();
        await LoadHistoryAsync();
    }

    [RelayCommand]
    private async Task LoadHistoryAsync()
    {
        if (_loaded)
        {
            return;
        }

        _loaded = true;

        try
        {
            var records = await _chat.GetRecentAsync(LoadLimit);

            foreach (var record in records)
            {
                Messages.Add(ChatBubble.FromRecord(record));
            }
        }
        catch (Exception ex)
        {
            Messages.Add(ChatBubble.System($"历史没读出来：{ex.Message}"));
        }
    }

    /// <summary>
    /// 清空对话历史。
    ///
    /// 【为什么要多问一句】
    /// 这是这个界面上唯一的破坏性操作，而且一键就能抹掉几十条。
    /// 误点之后要能反悔——所以是软删，不是真删。
    /// </summary>
    [RelayCommand]
    private async Task ClearHistoryAsync()
    {
        var page = CurrentPage;
        if (page is null)
        {
            return;
        }

        var ok = await page.DisplayAlertAsync(
            "清空对话", "之前的对话会全部收起来，不再显示。", "清空", "再想想");

        if (!ok)
        {
            return;
        }

        try
        {
            await _chat.ClearAsync();
            Messages.Clear();
        }
        catch (Exception ex)
        {
            await page.DisplayAlertAsync("没清成", ex.Message, "好");
        }
    }

    /// <summary>
    /// 存一条消息。
    ///
    /// 【为什么存失败要在界面上说】
    /// 用户要的就是"留得住"。存不进去却什么都不说，
    /// 他会一直以为留住了，直到某天打开发现是空的。
    /// </summary>
    private async Task SaveAsync(string role, string text, Guid? batchId, string? outcomesJson)
    {
        try
        {
            await _chat.AddAsync(role, text, batchId, outcomesJson);
        }
        catch (Exception ex)
        {
            Messages.Add(ChatBubble.System($"这条没存下来：{ex.Message}"));
        }
    }

    private static string? SerializeOutcomes(IReadOnlyList<ActionOutcomeDto> outcomes) =>
        outcomes.Count == 0 ? null : JsonSerializer.Serialize(outcomes);

    private static Page? CurrentPage =>
        Application.Current?.Windows.Count > 0 ? Application.Current.Windows[0].Page : null;

    // ================= 说话 =================

    /// <summary>
    /// 把当前输入框的内容发出去。有未回答的问题时，答案会一起带过去。
    ///
    /// 【为什么"再拆一次"不单独做一个按钮】
    /// 界面上每多一个按钮就多一个决策点。用户已经知道这个按钮是"发消息"，
    /// 换文字比加按钮便宜，也不存在"两个都能点，点哪个"的困惑。
    /// </summary>
    [RelayCommand]
    private async Task SendAsync()
    {
        var text = InputText.Trim();

        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        if (IsBusy)
        {
            return;
        }

        // 上一轮的猜测在这一轮发出之后就过期了：
        // 它们猜的是"看到上一条回复的你"，留着会被误点第二次。
        ClearSuggestions();

        Messages.Add(ChatBubble.User(text));
        InputText = string.Empty;

        await SaveAsync(ChatMessage.RoleUser, text, null, null);
        await RunAsync(text);
    }

    private async Task RunAsync(string userText)
    {
        IsBusy = true;
        StatusText = "AI 正在想……";

        try
        {
            var health = await _guard.EnsureAsync(s => StatusText = s);

            if (!health.Ok)
            {
                IsServerReady = false;
                Messages.Add(ChatBubble.System(
                    $"连不上后端（{_guard.DisplayAddress}）：{health.ErrorMessage}"));
                StatusText = "连不上后端";
                return;
            }

            IsServerReady = true;

            if (!health.AiReady)
            {
                Messages.Add(ChatBubble.System(
                    "后端起来了，但 AI 还没配好（appsettings.Local.json 里缺 Ai:ApiKey）。"));
                StatusText = "AI 没配好";
                return;
            }

            // 历史要带上：不延续上下文就了解不到实时情况（决策 D9）。
            // 只带最近若干条，超出部分截断。
            var history = Messages
                .Where(x => !x.IsSystem)
                .TakeLast(HistoryLimit)
                .Select(x => x.IsUser
                    ? ChatMessageDto.User(x.Text)
                    : ChatMessageDto.Assistant(x.Text))
                .ToList();

            // 用户这句已经在 history 的最后一条里了，
            // 但显式再补一次更保险——万一上面 TakeLast 因为并发错过了它。
            if (history.Count == 0 || history[^1].Content != userText)
            {
                history.Add(ChatMessageDto.User(userText));
            }

            var turn = await _api.AgentAsync(history, DateOnly.FromDateTime(DateTime.Now));

            if (!turn.Ok)
            {
                Messages.Add(ChatBubble.System($"AI 没回上来：{turn.Error}"));
                StatusText = "AI 没回上来";
                return;
            }

            var bubble = ChatBubble.Ai(turn);
            Messages.Add(bubble);

            // 改动明细和批次号一起存：批次号是"永久可撤"的钥匙（D4），
            // 重开之后那个「撤回」按钮还得能点。
            await SaveAsync(
                ChatMessage.RoleAssistant,
                turn.Reply,
                turn.BatchId,
                SerializeOutcomes(turn.Outcomes));

            StatusText = turn.Changed
                ? $"改了 {turn.Outcomes.Count} 条"
                : "说完了";

            ShowSuggestions(turn.Suggestions);

            // 动了数据就要让日程页立刻看到。
            // 不能指望切 Tab 时自动刷新——Shell 切 Tab 不保证触发 OnAppearing。
            if (turn.Changed)
            {
                WeakReferenceMessenger.Default.Send(new ItemsChangedMessage());
            }
        }
        catch (Exception ex)
        {
            Messages.Add(ChatBubble.System($"出错了：{ex.Message}"));
            StatusText = "出错了";
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// 点一下候选回复就直接发出去。
    ///
    /// 【为什么不再让他按一次发送】
    /// 若只是把这句话填进输入框，中间就隔着"再确认一次要不要发"——
    /// 而他点这个按钮的时候就已经确认了。多一次点击，就多一个可以放弃的路口。
    /// </summary>
    [RelayCommand]
    private async Task PickSuggestionAsync(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        if (IsBusy)
        {
            return;
        }

        InputText = text;
        await SendAsync();
    }

    /// <summary>
    /// 装上这一轮的候选回复。
    ///
    /// 【为什么限长限条不在这里各写一遍】
    /// 收拾的具体做法放在 Core 的 SuggestionCleaner 里，两边共用：
    /// 后端已经兜过一层，但历史记录、换模型都可能绕过去，
    /// 而这一排 chip 失控的表现是"把输入框顶没了"——代价太大，值得两端都拦。
    /// 两边文案不一致比不做更糟：后端截断到 24 字、前端截断到 15 字，
    /// 排出来的 chip 和冒烟测试对不上，改的时候也不知道该改哪一处。
    /// </summary>
    private void ShowSuggestions(IEnumerable<string>? items)
    {
        Suggestions.Clear();

        var cleaned = SuggestionCleaner.Clean(items);

        if (cleaned is not null)
        {
            foreach (var text in cleaned)
            {
                Suggestions.Add(text);
            }
        }

        OnPropertyChanged(nameof(HasSuggestions));
    }

    private void ClearSuggestions()
    {
        Suggestions.Clear();
        OnPropertyChanged(nameof(HasSuggestions));
    }

    // ================= 撤回 =================

    /// <summary>
    /// 撤回某一批改动。
    ///
    /// 【为什么撤销在对话页里，而不是藏在设置里】
    /// 发现 AI 改错的那一刻，你就在这一屏上，
    /// 而且最不想做的事就是再跟它聊一轮说"你帮我撤回"。
    /// 所以每一条带改动的回复下面都有一个「撤回」，就地解决。
    /// </summary>
    [RelayCommand]
    private async Task UndoAsync(ChatBubble bubble)
    {
        if (bubble.BatchId is null || bubble.IsUndone)
        {
            return;
        }

        var result = await _api.UndoAsync(bubble.BatchId.Value);

        if (result.UndoneCount > 0)
        {
            bubble.MarkUndone();
            WeakReferenceMessenger.Default.Send(new ItemsChangedMessage());

            // 记下来"这一批已经撤过"，否则重开之后按钮又变回可点，
            // 点下去什么都不会发生——能点但没反应比没有按钮更让人困惑。
            try
            {
                await _chat.MarkUndoneAsync(bubble.BatchId.Value);
            }
            catch (Exception ex)
            {
                Messages.Add(ChatBubble.System($"撤回了，但状态没记住：{ex.Message}"));
            }

            // 有冲突时语气要留余地：撤了一部分，剩下的没动。
            // 只说"撤回了"会让人以为全撤了，结果还有几条是改过的。
            StatusText = result.HasConflicts
                ? $"撤回了 {result.UndoneCount} 条，另有 {result.Conflicts.Count} 条没撤"
                : $"撤回了 {result.UndoneCount} 条改动";

            if (result.HasConflicts)
            {
                Messages.Add(ChatBubble.System(
                    "有一部分没撤回来：\n" + string.Join("\n", result.Conflicts)));
            }
        }
        else if (result.HasConflicts)
        {
            Messages.Add(ChatBubble.System(string.Join("\n", result.Conflicts)));
        }
        else
        {
            Messages.Add(ChatBubble.System(
                string.IsNullOrWhiteSpace(result.Message) ? "没撤成。" : result.Message));
        }
    }

    /// <summary>页面出现时先探一次后端，别等用户说完一整段才发现它没开。</summary>
    [RelayCommand]
    private async Task CheckServerAsync()
    {
        var health = await _guard.EnsureAsync(s => StatusText = s);
        IsServerReady = health.Ok && health.AiReady;

        StatusText = !health.Ok
            ? $"连不上后端：{health.ErrorMessage}"
            : !health.AiReady
                ? "后端起来了，AI 还没配好"
                : "准备好了";
    }
}

/// <summary>
/// 一条气泡。
///
/// 【为什么要区分"系统消息"】
/// 连不上服务器、撤销失败这类话不是 AI 说的，也不是用户说的。
/// 混在对话里会让人以为是 AI 的回答，那就把故障当成了内容。
/// 所以给它单独的样式位（IsSystem），界面上用不同的颜色和位置显示。
/// </summary>
public partial class ChatBubble : ObservableObject
{
    private ChatBubble()
    {
        Text = string.Empty;
    }

    public bool IsUser { get; private init; }
    public bool IsSystem { get; private init; }
    public string Text { get; private init; }

    /// <summary>这一轮 AI 实际改动的条目。没改数据就为空。</summary>
    public List<ActionOutcomeDto> Outcomes { get; private init; } = new();

    public Guid? BatchId { get; private init; }

    [ObservableProperty]
    public partial bool IsUndone { get; set; }

    public bool HasOutcomes => Outcomes.Count > 0;

    /// <summary>
    /// 对齐方式。你说的靠右，AI 说的靠左，系统提示靠左且不装进气泡。
    ///
    /// 【为什么返回的是 LayoutOptions 而不是字符串 "End"/"Start"】
    /// 返回字符串要靠 XAML 的类型转换，万一转换不生效，
    /// 表现是所有气泡一律靠左——不报错，只是看起来不对，很难查。
    /// 直接给对类型就没有这个环节。
    /// </summary>
    public LayoutOptions Align =>
        IsUser ? LayoutOptions.End
        : IsSystem ? LayoutOptions.Center
        : LayoutOptions.Start;

    /// <summary>系统提示不装进气泡框——它是对话之外的故障说明。</summary>
    public bool ShowAsBubble => !IsSystem;

    /// <summary>
    /// 气泡底色。
    ///
    /// 【为什么气泡要自己给底色，而不是直接用页面底色】
    /// 气泡的作用是"把一轮对话圈出来"，没有底色的话长对话里分不清哪几段是一句。
    ///
    /// 【为什么底色和文字色必须由同一处决定】
    /// 这两个值是一对：底色变深了、文字还是黑的，整段话就消失在气泡里。
    /// 曾经这里写死浅底 + XAML 里写死黑字，深色模式一开就是"深底黑字"。
    /// 所以两个值都从 AppColors 取，主题一变两个一起变，不可能只变一半。
    /// </summary>
    public Color BubbleColor => IsUser ? AppColors.BubbleSelf : AppColors.BubbleAi;

    /// <summary>
    /// 气泡里的文字色。跟上面的底色配对，不能跟正文走——
    /// 正文在深色下是浅色，而气泡底色是"比页面亮一点"，配浅字就糊了。
    /// </summary>
    public Color BubbleTextColor => AppColors.OnBubble;

    /// <summary>
    /// 还没被撤回、且确实有改动时才显示撤回按钮。
    /// 撤过一次的批次不能再撤第二次——快照已经写回去了，再撤是空操作，
    /// 按钮留着只会让人以为还能点出什么。
    /// </summary>
    public bool CanUndo => BatchId is not null && !IsUndone;

    public string UndoText => IsUndone ? "已撤回" : $"撤回这 {Outcomes.Count} 条改动";

    public void MarkUndone() => IsUndone = true;

    public static ChatBubble User(string text) =>
        new() { IsUser = true, Text = text };

    public static ChatBubble System(string text) =>
        new() { IsSystem = true, Text = text };

    public static ChatBubble Ai(AgentTurn turn) =>
        new()
        {
            IsUser = false,
            Text = turn.Reply,
            Outcomes = turn.Outcomes,
            BatchId = turn.BatchId
        };

    /// <summary>
    /// 从库里的一条记录还原成气泡。
    ///
    /// 【为什么反序列化失败要吞掉而不是让它冒出去】
    /// 明细列表只是"这一轮改了什么"的展示信息，
    /// 它坏了不该导致整段历史读不出来——那样一条脏记录就会让整个对话页空白。
    /// 明细丢了，撤回按钮还能用（靠的是 BatchId，不是 JSON）。
    /// </summary>
    public static ChatBubble FromRecord(ChatMessage record)
    {
        var outcomes = new List<ActionOutcomeDto>();

        if (!string.IsNullOrWhiteSpace(record.OutcomesJson))
        {
            try
            {
                outcomes = JsonSerializer.Deserialize<List<ActionOutcomeDto>>(record.OutcomesJson)
                           ?? new List<ActionOutcomeDto>();
            }
            catch (JsonException)
            {
                // 明细坏了就当没有，气泡照常显示。
            }
        }

        return new ChatBubble
        {
            IsUser = record.Role == ChatMessage.RoleUser,
            Text = record.Text,
            Outcomes = outcomes,
            BatchId = record.BatchId,
            IsUndone = record.IsUndone
        };
    }
}
