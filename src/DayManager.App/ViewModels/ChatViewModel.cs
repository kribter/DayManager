using System.Collections.ObjectModel;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using DayManager.App.Messages;
using DayManager.App.Drawables;
using DayManager.App.Services;
using DayManager.Core.Ai;
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

    /// <summary>AI 没回上来时，最多再把同一句原样重问几遍（不含第一次）。</summary>
    private const int MaxRetries = 2;

    /// <summary>两次之间隔多久。1 秒够后端缓过来，又不至于让人干等着。</summary>
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(1);

    private readonly ApiClient _api;
    private readonly BackendGuard _guard;
    private readonly ChatRepository _chat;
    private readonly AiSettings _ai;

    /// <summary>历史只加载一次。切 Tab 回来时 Shell 会再触发，重复加载会把列表翻倍。</summary>
    private bool _loaded;

    /// <summary>
    /// 界面上已经显示了多少条**库里的**记录。翻页时当 skip 用。
    /// 从集合现算，而不是自己维护一个计数器：新消息追加、单条删除、清空都会改变它，
    /// 计数器漏加一次，下一页就会把同一条再捞一遍。
    /// 系统提示（连不上后端之类）没有 id、也不在库里，正好不算。
    /// </summary>
    private int ShownCount => Messages.Count(x => x.Id != Guid.Empty);

    /// <summary>还有没有更早的消息没翻出来。</summary>
    [ObservableProperty]
    public partial bool HasMore { get; set; }

    /// <summary>正在翻上一页。防连点，也用来告诉页面别在这期间滚到底。</summary>
    [ObservableProperty]
    public partial bool IsLoadingMore { get; set; }

    /// <summary>正在往列表**顶部**插旧消息。页面读到它就跳过"新消息滚到底"。</summary>
    public bool IsPrepending { get; private set; }

    public ChatViewModel(ApiClient api, BackendGuard guard, ChatRepository chat, AiSettings ai, AvatarService avatars)
    {
        _api = api;
        _guard = guard;
        _chat = chat;
        _ai = ai;
        AiAvatar = new AvatarDrawable(avatars.Ai);
        MyAvatar = new AvatarDrawable(avatars.Me);

        StatusText = "还没说话";

        // 「清空」按钮只在聊过之后出现，集合变了要通知一次，不然它永远不显示。
        // 日期条也一样：从顶部插入一批旧消息后，分界点会整段往后移，
        // 必须按新的顺序重算一遍，不能只给新来的那几条算。
        Messages.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasMessages));
            RefreshDayDividers();
        };

        // AI 主动说的话要能立刻看见。它是在后台线程上写进库的，
        // 不订阅就只有下次重新进这个页面时才出现——
        // 而"通知弹了、打开对话页却找不到那句话"是最让人怀疑自己记错的情况。
        WeakReferenceMessenger.Default.Register<ProactiveSpokeMessage>(
            this, (_, _) => _ = AppendLatestAsync());

        // 气泡的底色和文字色是每个气泡取值的那一瞬间算的（BubbleColor / BubbleTextColor），
        // 没人重新取它就永远停在旧值。换主题时对每个气泡叫一遍，让绑定重取。
        WeakReferenceMessenger.Default.Register<ThemeChangedMessage>(
            this, (_, _) => RefreshBubblesForTheme());

        // 右键菜单的「删除这一句」从原生层发过来（菜单挂在 WinUI 控件上，
        // 平台代码拿不到这个 ViewModel）。收到就按命令的规矩走：软删、不弹确认。
        WeakReferenceMessenger.Default.Register<DeleteChatBubbleMessage>(
            this, (recipient, m) => ((ChatViewModel)recipient).DeleteCommand.Execute(m.Bubble));
    }

    /// <summary>
    /// 通知所有气泡：主题变了，颜色重新取一遍。
    ///
    /// 【为什么不在收到消息时顺手把页面级的东西也刷】
    /// 页面级（背景、状态条）走的是 XAML 的 AppThemeBinding，框架自己跟；
    /// 这里只管 C# 里算出来的那几个值。设置页返回对话页时 ChatPage.OnAppearing
    /// 还会再调一次本方法兜底——消息万一没送到，回到页面时也能修正。
    /// </summary>
    public void RefreshBubblesForTheme()
    {
        foreach (var bubble in Messages)
        {
            bubble.RefreshTheme();
        }
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
    /// 两侧头像各一支笔。全部气泡共用这两个实例：
    /// 它们不存"某个气泡的头像"，只是每次重绘时去问一次"现在是哪张图"。
    ///
    /// 【为什么放在 ViewModel 上而不是每个气泡一份】
    /// 气泡是数据，笔是画法。五十个气泡各造一支笔，
    /// 换头像时要挨个通知一遍——放在外面就只需要在页面上重画一次。
    /// </summary>
    public AvatarDrawable AiAvatar { get; }

    /// <summary>同上，你自己这一侧。</summary>
    public AvatarDrawable MyAvatar { get; }

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

            // 用总数判断，而不是"这一页满没满"：刚好满一页时后面可能还有，
            // 也可能正好没了，模棱两可的按钮最烦人。
            HasMore = await _chat.CountAsync() > ShownCount;
        }
        catch (Exception ex)
        {
            Messages.Add(ChatBubble.System($"历史没读出来：{ex.Message}"));
        }
    }

    /// <summary>
    /// 再翻一页更早的消息，插到列表**顶部**。
    ///
    /// 【为什么整窗重取再按 id 去重】
    /// 见 ChatRepository.GetRecentAsync 的说明：期间新来的消息、删掉的一条，
    /// 都会让"跳过前 N 条"这种分页错一格。整窗取回来，只挑没显示过的，
    /// 顺序天然对、也不会重复。
    ///
    /// 【为什么要先打招呼再插】
    /// 往 ScrollView 顶部塞内容，视口会往回跳。页面收到开始消息先量好位置，
    /// 插完之后按新增高度补回去，用户看的那条还停在原处。
    /// </summary>
    [RelayCommand]
    private async Task LoadMoreHistoryAsync()
    {
        if (IsLoadingMore || !HasMore || !_loaded)
        {
            return;
        }

        IsLoadingMore = true;
        IsPrepending = true;

        try
        {
            WeakReferenceMessenger.Default.Send(new ChatHistoryPrependStarted());

            // 窗口 = 已经显示的条数 + 一整页。系统提示不在库里，ShownCount 不算它们。
            var window = await _chat.GetRecentAsync(ShownCount + LoadLimit);
            var shown = Messages.Where(x => x.Id != Guid.Empty).Select(x => x.Id).ToHashSet();
            var older = window.Where(x => !shown.Contains(x.Id)).ToList();

            // 从最旧的一条开始插：倒着插进 index 0，最终顺序才是从早到晚。
            for (var i = older.Count - 1; i >= 0; i--)
            {
                Messages.Insert(0, ChatBubble.FromRecord(older[i]));
            }

            // 同样用总数收口：库里还有没显示过的，按钮就继续在。
            HasMore = await _chat.CountAsync() > ShownCount;
        }
        catch (Exception ex)
        {
            Messages.Add(ChatBubble.System($"更早的消息没读出来：{ex.Message}"));
        }
        finally
        {
            IsPrepending = false;
            IsLoadingMore = false;
            // 插入期间每次 CollectionChanged 都被 IsPrepending 挡掉了，
            // 这里补一次全量重算，把新接上的那一段日期分界对齐。
            RefreshDayDividers();
            WeakReferenceMessenger.Default.Send(new ChatHistoryPrepended());
        }
    }

    /// <summary>
    /// 重算每条气泡上方要不要显示日期条。
    ///
    /// 【为什么每次集合变了都整段重算】
    /// 从顶部插一批旧消息之后，分界点会整段往后移：原来第一条顶着的"今天"，
    /// 现在可能变成中间某条，而真正该顶日期条的是新插进来的最旧一条。
    /// 只给新来的算会留下一条假的旧分界，整段重算最省心也最不容易错。
    ///
    /// 【系统提示不参与】
    /// 连不上后端、删除失败这类话不是对话内容，也没有时间；
    /// 让它顶一条日期条会把"哪天开始的对话"说错。
    /// </summary>
    private void RefreshDayDividers()
    {
        if (IsPrepending)
        {
            return;
        }

        var now = DateTime.Now;
        DateOnly? previousDay = null;

        foreach (var bubble in Messages)
        {
            if (bubble.IsSystem || bubble.CreatedAt is null)
            {
                bubble.SetDayDivider(false, string.Empty);
                continue;
            }

            var day = ChatTime.LocalDay(bubble.CreatedAt.Value);
            var local = bubble.CreatedAt.Value.ToLocalTime().DateTime;
            var needsDivider = previousDay != day;

            bubble.SetDayDivider(needsDivider,
                needsDivider ? ChatTime.DayDivider(local, now) : string.Empty);

            previousDay = day;
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
            // 清空之后没有"更早的"了，按钮要跟着消失，
            // 否则点一下只会白查一次库、什么也没翻出来。
            HasMore = false;
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

    /// <summary>
    /// 删掉单独一条。
    ///
    /// 【为什么不弹"确定吗"】
    /// 清空全部那样一键抹几十条才需要再确认一次；
    /// 删一条的判断已经由"你点的就是这一条"承担了。
    /// 再问一遍是在暗示这一步有后果，而它其实没有：
    /// 删掉的是话，AI 做过的改动不受影响（要反悔数据，走「撤回」）。
    ///
    /// 【为什么 AI 说的也能删】
    /// 答非所问的那一次、夹在中间碍眼的那一句，留着只会让整段对话更难读。
    /// </summary>
    [RelayCommand]
    private async Task DeleteAsync(ChatBubble bubble)
    {
        // 还没落库的（Guid.Empty）本来就不在数据库里，从界面上拿掉就是全部。
        if (bubble.Id != Guid.Empty)
        {
            try
            {
                await _chat.DeleteAsync(bubble.Id);
            }
            catch (Exception ex)
            {
                Messages.Add(ChatBubble.System($"没删掉：{ex.Message}"));
                return;
            }
        }

        Messages.Remove(bubble);
    }

    private async Task<ChatMessage?> SaveAsync(string role, string text, Guid? batchId, string? outcomesJson)
    {
        try
        {
            return await _chat.AddAsync(role, text, batchId, outcomesJson);
        }
        catch (Exception ex)
        {
            Messages.Add(ChatBubble.System($"这条没存下来：{ex.Message}"));
            return null;
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

        var bubble = ChatBubble.User(text);
        Messages.Add(bubble);
        InputText = string.Empty;

        // 先把它说出去、再补"落地"的那部分信息。
        // 有了 id 这一条才删得动，有了时间界面才标得出它是几点说的。
        var saved = await SaveAsync(ChatMessage.RoleUser, text, null, null);
        if (saved is not null)
        {
            bubble.AttachRecord(saved);
        }

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

            // 【为什么不能只看 health.AiReady】
            // 那是"服务端配置文件里有没有 key"——密钥改成界面填之后，
            // 服务端那份是刻意留空的（否则打包发出去等于送钥匙）。
            // 真正的问题是"这次请求有没有钥匙可用"：服务端有，或
            // 时间页填的那把会随请求带上，都算就绪。真填错了，上游的
            // 401 会顺着这条链路显示出来，比在这里猜更准。
            if (!health.AiReady && !_ai.HasKey)
            {
                Messages.Add(ChatBubble.System(
                    "还没有 AI 密钥。到时间页的「AI 接入」里填上你自己的密钥（填完点「保存并试一下」就能验证）。"));
                StatusText = "AI 密钥没填";
                return;
            }

            // 历史要带上：不延续上下文就了解不到实时情况（决策 D9）。
            // 只带最近若干条，超出部分截断。
            // 【为什么每条要带 CreatedAt】
            // 后端要用它把「这条是几点说的 / 距现在隔了多久 / 是不是跨了一夜」
            // 由代码算好、写成看得见的字再喂给模型。只给一个「现在 HH:mm」让它自己推，
            // 实测会把昨晚深夜和今早当成连续一场，上午还接着说晚安。
            var history = Messages
                .Where(x => !x.IsSystem)
                .TakeLast(HistoryLimit)
                .Select(x => x.IsUser
                    ? ChatMessageDto.User(x.Text, x.CreatedAt)
                    : ChatMessageDto.Assistant(x.Text, x.CreatedAt))
                .ToList();

            // 用户这句已经在 history 的最后一条里了，
            // 但显式再补一次更保险——万一上面 TakeLast 因为并发错过了它。
            if (history.Count == 0 || history[^1].Content != userText)
            {
                history.Add(ChatMessageDto.User(userText, DateTimeOffset.Now));
            }

            // 【为什么只对「向后端要这一句话」重发】
            // 连不上后端、没填密钥是更早的提前返回，重发没有意义；
            // 而一旦成功拿到结果（turn.Ok）就立刻停，绝不重发——
            // 那一轮可能已经改了数据，再问一遍会把动作做两次。
            // 每轮之间隔 1 秒，最多再问两遍，总共等 2 秒。
            var today = DateOnly.FromDateTime(DateTime.Now);
            AgentTurn? okTurn = null;
            string? lastError = null;

            for (var attempt = 0; attempt <= MaxRetries; attempt++)
            {
                if (attempt > 0)
                {
                    await Task.Delay(RetryDelay);
                }

                try
                {
                    var one = await _api.AgentAsync(history, today);

                    if (one.Ok)
                    {
                        okTurn = one;
                        break;
                    }

                    lastError = one.Error;
                }
                catch (Exception ex)
                {
                    // 连不上、超时也算这一轮没回上来，跟 ok=false 一样处理。
                    // 中间几次失败不弹气泡、不写库——这一轮对话只有一条你说的话。
                    lastError = ex.Message;
                }
            }

            if (okTurn is null)
            {
                // 沿用原来那句文案，气泡里不多解释；原话放回输入框，
                // 直接点发送就能再来一次，不用复制粘贴重打一遍。
                Messages.Add(ChatBubble.System($"AI 没回上来：{lastError ?? "没收到回复"}"));
                StatusText = "AI 没回上来";
                InputText = userText;
                return;
            }

            var turn = okTurn;
            var bubble = ChatBubble.Ai(turn);
            Messages.Add(bubble);

            // 改动明细和批次号一起存：批次号是"永久可撤"的钥匙（D4），
            // 重开之后那个「撤回」按钮还得能点。
            var saved = await SaveAsync(
                ChatMessage.RoleAssistant,
                turn.Reply,
                turn.BatchId,
                SerializeOutcomes(turn.Outcomes));

            // 存完就能把 id 和时间填回去，这一条从此可以单独删除、也标得出时刻。
            if (saved is not null)
            {
                bubble.AttachRecord(saved);
            }

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
            InputText = userText;
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
        IsServerReady = health.Ok && (health.AiReady || _ai.HasKey);

        StatusText = !health.Ok
            ? $"连不上后端：{health.ErrorMessage}"
            : !IsServerReady
                ? "后端起来了，AI 密钥还没填（时间页「AI 接入」）"
                : health.AiReady
                    ? "准备好了"
                    : "准备好了（用你填的密钥）";
    }
}
