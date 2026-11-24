using System;
using System.Collections.Generic;
using System.Text.Json;
using DayManager.Api.Models;
using DayManager.Core.Ai;
using DayManager.Core.Enums;
using DayManager.Core.Planning;
using DayManager.Core.Profile;
using DayManager.Data.Repositories;

namespace DayManager.Api.Services;

/// <summary>
/// 「会动手的 AI」。
///
/// 【和 AiProxy 的分工】
/// AiProxy 只负责把话传过去、把回答拿回来，它不知道日程是什么。
/// 这里负责三件它做不到的事：
///   1. 把当前日程状况**喂给模型**（不然它只能凭空编）；
///   2. 把模型说的"把这三件事推到明天"**翻译成真的操作**；
///   3. 把执行结果**说清楚**（做了哪几条、有没有失败的、批次号是多少）。
///
/// 【为什么上下文要在服务端拼，而不是让客户端把清单传上来】
/// 客户端当然也拿得到清单，但那样每加一种上下文（使用时长、画像、干预记录）
/// 都要改一次客户端的请求结构，而且客户端要自己决定"传多少条"。
/// 服务端直接读库，永远是完整且最新的，也不会因为客户端版本旧而缺字段。
/// </summary>
public sealed class AgentService
{
    private readonly AiProxy _proxy;
    private readonly ItemTools _tools;
    private readonly ItemRepository _items;
    private readonly ProjectRepository _projects;
    private readonly PolicyRepository _policy;
    private readonly ProfileService _profile;
    private readonly ILogger<AgentService> _logger;
    private readonly AgentPromptBuilder _promptBuilder;
    private readonly WebSearchService _search;
    private readonly WeatherService _weather;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true
    };

    /// <summary>
    /// 最多允许几次「查完再答」的回喂。1 = 最多两次模型调用。
    ///
    /// 【为什么必须封顶】
    /// 模型可能每一轮都再发一个 query（觉得还没看够），不封顶就是无限烧 token。
    /// 一次查询已经能覆盖 31 天，足够回答"更早的某天有什么"。
    /// </summary>
    private const int MaxQueryRounds = 1;

    public AgentService(
        AiProxy proxy,
        ItemTools tools,
        ItemRepository items,
        ProjectRepository projects,
        PolicyRepository policy,
        ProfileService profile,
        AgentPromptBuilder promptBuilder,
        WebSearchService search,
        WeatherService weather,
        ILogger<AgentService> logger)
    {
        _proxy = proxy;
        _tools = tools;
        _items = items;
        _projects = projects;
        _policy = policy;
        _profile = profile;
        _promptBuilder = promptBuilder;
        _search = search;
        _weather = weather;
        _logger = logger;
    }

    /// <summary>
    /// 一轮对话：说完话，该动手的顺便动手。
    /// </summary>
    public async Task<AgentResult> ChatAsync(
        IReadOnlyList<ChatMessage> history,
        DateOnly? today = null,
        AiOverride? ai = null,
        CancellationToken ct = default)
    {
        var day = today ?? DateOnly.FromDateTime(DateTime.Now);

        // ---------- ① 准备上下文 ----------

        var items = await _items.GetDayListAsync(day, ct);
        var stats = await _items.GetDayStatsAsync(day, ct);

        // 「一直悬着、还没到截止日的事」单独取。它们不在【今天的清单】里
        // （不算今天该做完），可 AI 必须看得见：5.2 定了这类事允许提醒、给动力，
        // 而且「软件里所有信息 AI 都能看到」是已经修过的口径。
        // 走同一个 GetFloatingDeadlinesAsync，界面和 AI 才是同一份事实。
        var floating = await _items.GetFloatingDeadlinesAsync(day, ct);

        // 阶段性目标（容器）同样不进今日清单，但也得让模型看见：
        // 否则他刚说过「第一章」，下一句问「我那个竞赛拆到哪了」，
        // AI 会答「没看到」，正好破坏「软件里所有信息 AI 都能看到」。
        var stages = await _items.GetStagesAsync(ct);

        // 大任务（项目）和挂在它们下面的任务，同样不进今日清单，
        // 但他问「这条挂 aaa 还是 abc 下面」时必须答得出来。
        // 之前只喂了上面那段容器，模型视野里就只有一个孤零零的壳名，
        // 只好回「我看不到 / 你截个图给我」——那正是要修的那一处。
        // 只补视野：项目名、归属、项目下清单都从这一份事实里读，不加动作、不改模型。
        var projectGroups = await _projects.GetAllWithItemsAsync(ct);

        // 未来几天。
        //
        // 【为什么一天一天地查，而不是写一个"查某段日期"的新方法】
        // 一天该包含哪些条目，是有讲究的（逾期未完成会自动并进今天、
        // 放弃的不出现、跳过的不算），那套规则只有 GetDayListAsync 里那份是对的。
        // 另写一个范围查询，等于把这条规则抄第二遍——
        // 抄的那份迟早和原版不一致，而这类不一致的表现是"AI 说的和界面上看到的不一样"，
        // 极难查。七天就是七次查询，一个人用的数据量，这个代价换口径唯一，值。
        var upcoming = new List<AgentPromptBuilder.DayList>();
        for (var i = 1; i <= AgentPromptBuilder.UpcomingDays; i++)
        {
            var future = day.AddDays(i);
            upcoming.Add(new AgentPromptBuilder.DayList(future, await _items.GetDayListAsync(future, ct)));
        }

        // 过去几天的回顾。走的是 GetDayReviewAsync（那天实际发生了什么），
        // 不是界面翻页口径——后者会把同一条逾期欠账在 7 天里各带一遍。
        var review = new List<AgentPromptBuilder.DayList>();
        for (var i = AgentPromptBuilder.ReviewDays; i >= 1; i--)
        {
            var past = day.AddDays(-i);
            review.Add(new AgentPromptBuilder.DayList(past, await _items.GetDayReviewAsync(past, ct)));
        }

        // ---------- ② 拼提示 ----------

        var now = DateTime.Now;
        // 先物化一次：下面既要逐条标注，又要从「当前这句之前」算出距上次说话多久。
        var recentHistory = history.Skip(Math.Max(0, history.Count - 20)).ToList();

        var messages = new List<ChatMessage>
        {
            new() { Role = "system", Content = await _promptBuilder.Build(day, items, stats, upcoming, review, floating, stages, projectGroups, ct) }
        };

        // 只带最近几轮历史：全部带上的话，聊久了 token 会失控，
        // 而真正影响当前判断的只有最近这几句。
        // （更长的历史压缩进 daily_context 是 M9 的事）
        //
        // 【为什么每条要打时间标注】
        // 纯文本历史会让模型把昨晚深夜和今早当成连续一场（实测上午 10:57 还在说晚安）。
        // "隔了一夜 / 这条是几点说的"必须由代码算好、写成看得见的字，
        // 而不是留一个「现在 HH:mm」让它自己减。标注由 ChatTime 统一生成，
        // 主动开口链路用的是同一个函数，口径只有一份。
        foreach (var m in recentHistory)
        {
            messages.Add(new ChatMessage
            {
                Role = m.Role,
                Content = ChatTime.Mark(m.Content, m.CreatedAt, now),
                CreatedAt = m.CreatedAt
            });
        }

        // 【为什么坐标段要单列一条、且放在历史之后】
        // 模型对靠后的指令权重最高（项目里实测过多次）。把"现在是上午 /
        // 距上次说话隔了多久 / 旧状态默认过期"写成一段硬事实放在这里，
        // 是吃饭事件（18:03 去吃饭、20:10 还劝饭先吃）和催睡觉的直接解药。
        // 最后一条是用户本次说的这句，所以"上一句话"取它之前的最后一条有时间的。
        var lastBeforeCurrent = recentHistory.Count >= 2
            ? recentHistory.Take(recentHistory.Count - 1).LastOrDefault(x => x.CreatedAt.HasValue)?.CreatedAt
            : null;
        messages.Add(new ChatMessage
        {
            Role = "system",
            Content = ChatTime.Coordinate(now, lastBeforeCurrent)
        });

        // 【视野校正，11-22 16:36 截图实测】数据进了最前面的系统提示还不够：
        // 他在同一个会话里连着问「你现在能看到了吗」「你再看看？」，模型被自己前几轮
        // 说过的「看不到 aaa / abc」锚住，顺着旧结论又答了一遍「还是没有喵」。
        // 这类问的是「你自己的状态」，不是「某条挂哪」——所以光写「问归属照那段答」覆盖不到。
        // 靠后的一条 system 权重最高，且直接给出能引用的具体名字，比反复叮嘱「别说看不到」管用。
        var projectNames = string.Join("、", projectGroups.Take(5).Select(g => g.Project.Name));
        var visionNote = $"【视野校正】你现在看得见 {projectGroups.Count} 个大任务（项目），"
            + (string.IsNullOrEmpty(projectNames) ? "" : $"名字包括 {projectNames} 等，")
            + "完整清单和各自下面的任务在【大任务（项目）】段。阶段性目标是另一类壳，别混。";
        messages.Add(new ChatMessage
        {
            Role = "system",
            Content = visionNote
                + "他问「你现在能看到了吗」「你再看看」时，照这段答「能」，并说出具体项目名；"
                + "别说看不到、别让他截图。"
        });

        // 【为什么格式要求要在最后再说一遍】
        // 模型对**靠后**的指令权重最高。系统提示写在最前面，
        // 聊过几轮之后它的约束力会明显下降——实测就是这样：
        // 用户说"你只要回一句话"，它就真只回一句中文，连大括号都不给，
        // 哪怕 API 层已经开了 json_object 模式。
        // 在末尾补一句提醒能把它拉回来，比事后兜底有用得多。
        // 这条消息只发给模型，不进对话记录——它不是任何人说过的话。
        //
        // 【为什么措辞里没有"先确认"三个字】
        // 上一版写的是"回覆前先确认：输出必须是……"，
        // 模型把它理解成"把确认的过程写出来"，于是 reply 里出现了
        // "他只是在打招呼……保持两三句。actions 为空。"这种自言自语。
        // 现在改成直接说两个字段各放什么，不给它发挥的余地。
        messages.Add(new ChatMessage
        {
            Role = "system",
            Content = "输出必须是那个 JSON：think 放你的判断（只有你看得到），reply 放你要对他说的原话。"
                + "不要把你的想法、格式说明或任何备注写进 reply。不要输出 JSON 以外的任何文字。"
        });

        var request = new ChatRequest
        {
            Messages = messages,
            JsonMode = true,
            // 把调用者带来的密钥/地址/模型一路带下去：
            // 服务端自己未必配了密钥（分发时不能带），这条链路是唯一的来源。
            Ai = ai,
            // 【为什么 max_tokens 必须给足】
            // V4 系列会先消耗 reasoning token，再输出正式内容。
            // 给小了的表现不是报错，而是"调用成功但内容是空的"——
            // 最难查的那类问题。
            MaxTokens = 8000
        };

        // ---------- ③ 调模型：可能调两次（第一次只是「我要查一下」） ----------
        //
        // 【为什么这里成了循环】
        // 默认视野是「过去 7 天 + 今天 + 未来 7 天」。窗口外的日子模型看不到，
        // 但它可以声明一个只读的 query 动作，系统查完把清单回喂，它再出最终答复。
        // 这就是文档里 L1「工具结果回喂」的第一道门，也是「用户能看到的 AI 都能看到」
        // 这条原则第一次真正落地。查询只读：不写库、不进撤销批次、不经过 ItemTools。
        //
        // 【为什么要有上限】
        // 模型可能每轮都再发一个 query（觉得还没看够），不封顶就是无限烧 token。
        // 所以最多回喂 MaxQueryRounds 次；到上限仍要查，就忽略 query 直接给答复。
        AgentReply? reply = null;

        for (var round = 0; round <= MaxQueryRounds; round++)
        {
            var once = await CallOnceAsync(request, ct);
            if (once.Error is not null)
            {
                return AgentResult.Failed(once.Error);
            }

            var content = once.Content!;

            // 解析：JSON 里截一段、或干脆是纯文本，两条兜底都留着。
            var parsed = ParseReply(content);
            if (parsed is null)
            {
                // 它没按格式来。先试着从坏 JSON 里捞出「reply」那句话，捞不到就
                // 给一句能接住的话——绝不能把坏 JSON（连同 think 里的自言自语）
                // 原样甩给用户看。这种轮次不会有 query。
                var salvaged = ReplySalvage.Salvage(content);
                return new AgentResult
                {
                    Success = true,
                    Reply = string.IsNullOrWhiteSpace(salvaged)
                        ? "（这句没接好，你再说一遍？）"
                        : CleanReply(salvaged),
                    Outcomes = new List<ActionOutcome>(),
                    BatchId = null,
                    Suggestions = null
                };
            }

            reply = parsed;

            var queries = parsed.Actions.Where(IsQuery).ToList();
            var searches = parsed.Actions.Where(IsSearch).ToList();
            var weathers = parsed.Actions.Where(IsWeather).ToList();
            if ((queries.Count == 0 && searches.Count == 0 && weathers.Count == 0) || round == MaxQueryRounds)
            {
                break;
            }

            // 【为什么这一轮的其它动作一律不执行】
            // 它同时说「我要查」和「把那条删了」时，那个写动作是基于**还没查到**的信息做的。
            // 先读后写：这一轮只当查询/搜索/天气请求，查完让它重新给一次完整的 actions。
            var gathered = new List<string>();
            if (queries.Count > 0)
            {
                gathered.Add(await RunQueriesAsync(queries, ct));
            }
            if (searches.Count > 0)
            {
                // 搜索 key 跟聊天 key 一样随请求带来：谁在用，就烧谁的额度。
                gathered.Add(await RunSearchesAsync(searches, ai?.SearchApiKey, ct));
            }
            if (weathers.Count > 0)
            {
                gathered.Add(await RunWeathersAsync(weathers, ct));
            }

            var gatherText = string.Join("\n\n", gathered);

            // 把它这一轮的原话接回去，再接上系统查回来的结果。
            // 两条都只在本次请求里，不进对话记录——不是任何人说过的话。
            // 【为什么还要补一条格式提醒】
            // 模型对靠后的指令权重最高（项目里实测过多次）。末尾这条既说清
            // 「现在才是最终答复」，也把格式再压一遍，免得它把查库结果复述给用户。
            // assistant 那条只是它自己申请查询时写下的 JSON，不是对用户说过的话。
            // 不说破，第二轮它可能以为"我已经回过他了"，于是回一句空话或不肯重写 actions。
            messages.Add(new ChatMessage { Role = "assistant", Content = content });
            messages.Add(new ChatMessage
            {
                Role = "system",
                Content = "【系统按你的要求查回来的结果】\n" + gatherText
                    + "\n\n上面是系统查回来的真实结果（日程库或联网搜索），不是他说的原话。"
                    + "紧接着那条 assistant 消息只是你刚才申请查询/搜索时写的东西，**不算你已经回答过他**。"
                    + "现在给出**最终**答复：仍按原来的 JSON 格式，reply 放你要对他说的那句话，"
                    + "actions 里放这次真正要做的改动（刚才那一轮的其它动作没有执行，要重新给）。"
                    + "不要再发 query 或 search。"
                    + "搜索结果里没有的信息别自己补，拿不准就标「存疑」；关键事实说「据 XX」。"
                    + "不要把查回来的结果、你的判断或任何格式说明写进 reply。"
            });
            request.Messages = messages;
            request.JsonMode = true;
        }

        // ---------- ④ 执行 ----------
        // query 是只读动作，ItemTools 不认识它，必须在这里剔掉，
        // 否则会多出一条「不认识这个操作」的失败结果显示给他看。
        var actions = reply!.Actions.Where(a => !IsQuery(a) && !IsSearch(a) && !IsWeather(a)).ToList();
        var (outcomes, batchId) = actions.Count == 0
            ? (new List<ActionOutcome>(), (Guid?)null)
            : await _tools.ExecuteAsync(actions, Summarize(actions), ct);

        var said = CleanReply(reply.Reply);

        return new AgentResult
        {
            Success = true,
            // 万一清完变空（整句都是自言自语），给一句能接住的话，
            // 而不是让界面上出现一个空气泡——空的那一格比说错更让人不安。
            Reply = string.IsNullOrWhiteSpace(said)
                ? "（刚才那句没组织好）你说的是这个意思吗？"
                : said,
            Outcomes = outcomes,
            BatchId = batchId,
            Suggestions = SuggestionCleaner.Clean(reply.Suggestions)
        };
    }

    /// <summary>
    /// 剔除 reply 里混进来的"模型对自己说的话"。实际实现在 <see cref="ReplyCleaner"/>，
    /// 放在 Core 是为了让冒烟测试能直接验证它——这段逻辑改错了标点，
    /// 用户第一眼就看得见，不能只在真实对话里碰运气。
    /// </summary>
    private static string CleanReply(string reply) => ReplyCleaner.Clean(reply);

    /// <summary>
    /// 调一次模型，把「网络重试 + 空内容重试」都收在这里，返回 content 或失败原因。
    ///
    /// 【为什么单独抽出来】
    /// 加了「查完再答」之后，一轮对话最多会调 MaxQueryRounds+1 次模型。
    /// 两轮必须走同一套重试和空内容兜底，复制一遍迟早只改了一边。
    /// </summary>
    private async Task<(string? Content, string? Error)> CallOnceAsync(ChatRequest request, CancellationToken ct)
    {
        var upstream = await _proxy.ChatAsync(request, ct);

        // 网络闪断、上游 5xx/限流：隔半秒重发一次多半就好。
        if (!upstream.Success && IsTransientStatus(upstream.StatusCode))
        {
            await Task.Delay(500, ct);
            upstream = await _proxy.ChatAsync(request, ct);
        }

        if (!upstream.Success)
        {
            return (null, $"调用 AI 失败：{upstream.Body}");
        }

        var content = ExtractContent(upstream.Body);

        // 空内容：原样重试一次，再不行就摘掉 json 模式（理由见原注释，此处从略）。
        // 推理模型偶发返回 200 + 空字符串，重发通常就有内容了。
        for (var attempt = 2; attempt <= 3 && string.IsNullOrWhiteSpace(content); attempt++)
        {
            LogEmpty("空内容，重试", upstream, attempt);
            await Task.Delay(400, ct);

            if (attempt == 3)
            {
                request.JsonMode = false;
            }

            upstream = await _proxy.ChatAsync(request, ct);
            if (!upstream.Success)
            {
                break;
            }

            content = ExtractContent(upstream.Body);
        }

        if (string.IsNullOrWhiteSpace(content))
        {
            if (!upstream.Success)
            {
                return (null, $"调用 AI 失败：{upstream.Body}");
            }

            LogEmpty("三次都空，放弃", upstream, 4);
            return (null, "这一句 AI 没有接住。换个说法再说一次？");
        }

        return (content, null);
    }

    /// <summary>解析模型输出。JSON 里截一段、或干脆是纯文本，两条兜底都留着。</summary>
    private static AgentReply? ParseReply(string content)
    {
        try
        {
            return JsonSerializer.Deserialize<AgentReply>(content, JsonOpts);
        }
        catch (JsonException)
        {
            // 它可能前后带了别的话（"好的，结果是：{...}"），把中间那段 JSON 截出来再试。
            return TryExtractJson(content);
        }
    }

    private static bool IsQuery(AiAction action) =>
        string.Equals(action.Op?.Trim(), "query", StringComparison.OrdinalIgnoreCase);

    private static bool IsSearch(AiAction action) =>
        string.Equals(action.Op?.Trim(), "search", StringComparison.OrdinalIgnoreCase);

    private static bool IsWeather(AiAction action) =>
        string.Equals(action.Op?.Trim(), "weather", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 执行一批只读查询，把结果拼成一段给模型看的字。只读：不写库、不进撤销批次。
    ///
    /// 【为什么用 GetDayListAsync 而不是 GetDayReviewAsync】
    /// query 是「把界面翻到那天会看到的样子」还给它——用户能看到什么，它就能看到什么。
    /// 回顾段用 ReviewAsync 是怕逐日拼 7 天时旧账重复 7 遍；这里一次只查用户点名的
    /// 那几天，重复恰恰是界面的真实样子，不该替它抹掉，只要把「这是同一条」说破。
    /// </summary>
    private async Task<string> RunQueriesAsync(IReadOnlyList<AiAction> queries, CancellationToken ct)
    {
        var days = new SortedSet<DateOnly>();
        var notes = new List<string>();

        foreach (var q in queries)
        {
            var spec = DayQuery.Parse(q.Date, q.DateTo);
            if (!spec.Valid)
            {
                notes.Add($"- 有一条查询没接住：{spec.Error}。");
                continue;
            }

            if (spec.Clipped)
            {
                notes.Add($"- 查询范围超过 {DayQuery.MaxSpanDays} 天，已截到前 {DayQuery.MaxSpanDays} 天。");
            }

            foreach (var d in spec.Enumerate())
            {
                days.Add(d);
            }
        }

        if (days.Count == 0)
        {
            return notes.Count > 0
                ? string.Join("\n", notes) + "\n（这次没查到任何一天，别编。）"
                : "（这次没查到任何一天，别编。）";
        }

        var lines = new List<string>();
        foreach (var d in days)
        {
            var list = await _items.GetDayListAsync(d, ct);
            var head = $"{d:MM-dd}（{d:dddd}）";

            if (list.Count == 0)
            {
                lines.Add($"{head}：空");
                continue;
            }

            lines.Add(head + "\n" + string.Join('\n', list.Select(i =>
                $"  - id={i.Id} | {i.Title} | {StatusName(i.Status)}" +
                (i.StartAt is { } st ? $" | {st.ToLocalTime():HH:mm}" : "") +
                (i.ActualMinutes is > 0 ? $" | 实际 {i.ActualMinutes} 分钟" : "") +
                (i.EstimateMinutes is > 0 ? $" | 预计 {i.EstimateMinutes} 分钟" : ""))));
        }

        // 界面翻页口径会把未完成的旧账并进每一个过去的日子。不说破，
        // 模型会把同一条记录当成好几件不同的事。
        lines.Add("说明：上面是界面翻到那天会看到的样子。未完成的老账会在多个日子里重复出现，那是同一条记录，不是多件事；写着「空」是真的没安排，不是没查到。");
        // 常驻项不属于任何一天，按天查询天然查不到。不说破，模型容易把
        // 「查了几天都没这条」当成「这人没这件事」，甚至回一句「没查到」。
        lines.Add("另外，「一直悬着、还没到截止日的事」不属于任何一天，按天查询不会带出来；它们已经整段列在系统提示里，别因为这里没看到就说没有。");

        if (notes.Count > 0)
        {
            lines.InsertRange(0, notes);
        }

        return string.Join("\n", lines);
    }

    /// <summary>
    /// 执行一批联网搜索，把结果拼成一段给模型看的字。只读：不写库、不进撤销批次。
    /// 搜索失败（没配 key、超时、上游报错）由 WebSearchService 自己消化成一句说明，
    /// 这里不做额外处理——搜索是加分项，不能因为它坏了整轮对话。
    /// </summary>
    private async Task<string> RunSearchesAsync(
        IReadOnlyList<AiAction> searches, string? searchKey, CancellationToken ct)
    {
        var lines = new List<string>();

        foreach (var s in searches)
        {
            if (string.IsNullOrWhiteSpace(s.Query))
            {
                lines.Add("- 有一条搜索没接住：没给 query。");
                continue;
            }

            lines.Add($"【搜了：{s.Query}】\n{await _search.SearchAsync(s.Query, searchKey, ct)}");
        }

        return string.Join("\n\n", lines);
    }

    /// <summary>
    /// 执行一批天气查询，把结果拼成给模型看的字。只读：不写库、不进撤销批次。
    /// 天气走 Open-Meteo（结构化数据），不是网页搜索——网页搜索对「北京天气」
    /// 经常返回报纸首页链接、没有温度，所以单独接一个稳定的数据源。
    /// </summary>
    private async Task<string> RunWeathersAsync(IReadOnlyList<AiAction> weathers, CancellationToken ct)
    {
        var lines = new List<string>();

        foreach (var w in weathers)
        {
            if (string.IsNullOrWhiteSpace(w.Query))
            {
                lines.Add("- 有一条天气查询没接住：没给地点。");
                continue;
            }

            lines.Add($"【查了天气：{w.Query}】\n{await _weather.GetAsync(w.Query, ct)}");
        }

        return string.Join("\n\n", lines);
    }

    /// <summary>
    /// 系统提示词。
    ///
    /// 【为什么要在这里写清楚"跳过"和"放弃"的区别】
    /// 模型不知道这两个状态在我们的系统里后果相反（一个明天回来，一个永不再现）。
    /// 不写清楚，它遇到"今天不做"就会随便挑一个，
    /// 用户以为只是今天不做，结果那条任务再也不出现了——
    /// 这是最伤人的一类错误，因为它表现为"软件帮我弄丢了事情"。
    /// </summary>

    private static string StatusName(ItemStatus status) => status.ToDisplayName();

    /// <summary>给撤销列表用的一句话摘要，例如"改了 3 条"。</summary>
    private static string Summarize(IReadOnlyList<AiAction> actions)
    {
        var ops = actions.Select(x => x.Op?.ToLowerInvariant()).ToList();

        var verb = ops.Contains("create") ? "新建"
            : ops.Contains("delete") ? "删除"
            : ops.Contains("reschedule") ? "改期"
            : ops.Contains("set_status") ? "改状态"
            : "调整";

        return $"{verb} {actions.Count} 条";
    }

    /// <summary>
    /// 从一段可能夹着别的文字的回复里截出 JSON 再试一次。
    ///
    /// 【为什么要这一步】
    /// 模型偶尔会在 JSON 前后加一句"好的，结果是："，
    /// 整段直接解析必然失败，而里面的 JSON 其实是好的。
    /// 丢掉前后那些话就能救回来，比当成失败有用得多。
    /// </summary>
    private static AgentReply? TryExtractJson(string content)
    {
        var start = content.IndexOf('{');
        var end = content.LastIndexOf('}');

        if (start < 0 || end <= start)
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<AgentReply>(content[start..(end + 1)], JsonOpts);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// 从 OpenAI 格式的响应里取出模型说的话。
    ///
    /// 【为什么不用强类型反序列化整个响应】
    /// 不同供应商返回的字段细节有出入（有的多一个 reasoning_content，有的字段为 null）。
    /// 强类型绑定会因为一个没预料到的字段就整体失败，
    /// 而这里真正需要的只有 choices[0].message.content 这一个值。
    /// </summary>
    private static string? ExtractContent(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);

            if (!doc.RootElement.TryGetProperty("choices", out var choices) || choices.GetArrayLength() == 0)
            {
                return null;
            }

            var message = choices[0].GetProperty("message");
            if (!message.TryGetProperty("content", out var content))
            {
                return null;
            }

            // 【为什么不再退回 reasoning_content】
            // 这是推理模型（deepseek-flash 会返回 reasoning_content）。
            // 原写法是 content 为空就拿 reasoning 顶上，理由是"总比说 AI 没说话强"——
            // 但这个理由站不住：reasoning 是模型对自己说的话
            // （"用户在问什么""我应该怎么回答"），把它显示给用户，
            // 正是用户反馈的那个问题：AI 在自言自语。
            // 空就让它空，上层会给一句能接住的话，比给人看内心独白强。
            return content.ValueKind == JsonValueKind.String ? content.GetString() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// 429 和 5xx 值得重试；400/401/403 这类重发了也一样，不值得。
    /// 503 也不重试：它在本代理里还承担"没配密钥"的含义，重试只是白等半秒。
    /// </summary>
    private static bool IsTransientStatus(int statusCode) => AiProxy.IsTransient(statusCode);

    /// <summary>
    /// 记一条"空内容"。控制台和文件各一份：控制台给开发时看，
    /// 文件给"后端由客户端拉起、控制台没人看得见"的时候看。
    /// </summary>
    private void LogEmpty(string what, AiProxyResult upstream, int attempt)
    {
        var detail = $"finish_reason={ExtractFinishReason(upstream.Body)}"
                     + $"，usage={ExtractUsage(upstream.Body)}"
                     + $"，requestId={ExtractRequestId(upstream.Body)}";

        _logger.LogWarning("{What}（第 {Attempt} 次尝试）。{Detail}", what, attempt, detail);
        AiDiagnostics.Log($"[agent] {what}（第 {attempt} 次尝试）  {detail}");
    }

    /// <summary>
    /// 取 finish_reason。空内容时它是唯一的线索：
    /// "length" 表示额度被推理吃光了，"stop" 则是模型主动结束了却什么都没说。
    /// </summary>
    private static string ExtractFinishReason(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("choices", out var choices)
                && choices.GetArrayLength() > 0
                && choices[0].TryGetProperty("finish_reason", out var reason))
            {
                return reason.ValueKind == JsonValueKind.String ? reason.GetString() ?? "?" : "?";
            }
        }
        catch (JsonException)
        {
            // 解析不了就算了：这只是日志里的补充信息，不该因为它让整轮失败。
        }

        return "?";
    }

    /// <summary>
    /// 取 token 用量。重点看 completion_tokens 是不是已经顶到 max_tokens——
    /// 顶到了就说明推理把额度吃完了，该调 max_tokens 而不是重试。
    /// </summary>
    private static string ExtractUsage(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("usage", out var usage))
            {
                var completion = usage.TryGetProperty("completion_tokens", out var c) ? c.ToString() : "?";
                var prompt = usage.TryGetProperty("prompt_tokens", out var p) ? p.ToString() : "?";
                var reasoning = usage.TryGetProperty("reasoning_tokens", out var r) ? r.ToString() : "-";
                return $"completion={completion} prompt={prompt} reasoning={reasoning}";
            }
        }
        catch (JsonException)
        {
            // 同上，日志信息缺失不致命。
        }

        return "?";
    }

    /// <summary>
    /// 取上游请求 id。真要去问供应商"这次调用为什么是空的"，只有这个号能查。
    /// </summary>
    private static string ExtractRequestId(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("id", out var id))
            {
                return id.ValueKind == JsonValueKind.String ? id.GetString() ?? "?" : "?";
            }
        }
        catch (JsonException)
        {
            // 同上。
        }

        return "?";
    }


}
