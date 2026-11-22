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
    private readonly PolicyRepository _policy;
    private readonly ProfileService _profile;
    private readonly ILogger<AgentService> _logger;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true
    };

    /// <summary>
    /// 一件事每天最多能占多少分钟。排期时的硬上限。
    ///
    /// 【这个额度是"给这一件事的"，不是他一天的总容量】
    /// 这里曾经理解反了：写成了"他每天总共能给多少"，后果是当天已经有别的事时，
    /// 新拆的这件事会被压得几乎排不进去——而它本该有自己的两小时，
    /// 别的事占的是别的事的时间。**两件事各占两小时不等于一天只有两小时。**
    ///
    /// 那"一天排太满"靠什么防？不靠这个数。上下文里已经给了往后七天的清单，
    /// AI 排之前能看见那天已有什么，过载由它看了之后提出来（见排期规则那几条）。
    /// 用一个固定总数去防过载，代价是每件事都被压扁；用真实清单去判断，才是准的。
    ///
    /// 【这个值现在是写死的，后面必须换成从 policy_settings 读】
    /// 需求 2.7 定了：不存在"设一次就固定"的参数，这个额度要由 AI 据实际完成量校准。
    /// 那张表（policy_settings）M3 已经建好了，读写服务还没做，
    /// 所以这里先用一个保守起点顶着——冷启动必须有起点，这是 2.7 的第一条约束。
    /// 换成动态值时要一起改：这里读表，AI 调整后写回表里，并在调整时说明依据。
    /// 注意校准的是"一件事每天的上限"，别又把它当成全天容量去调。
    /// </summary>
    private const int PerTaskDailyMinutes = 120;

    /// <summary>
    /// 除了今天，再往前给模型看几天。
    ///
    /// 【为什么必须有这个数】
    /// 早期只把"今天"的清单喂给模型，后果是用户问"明天有什么"，它答不出来——
    /// 要么含糊带过，要么凭空编几条。
    /// 而"接下来几天我有哪些事"恰恰是最常见的一类问题：
    /// 人问它不是为了回忆，是为了**决定现在要不要动**。
    /// 答不出来的 AI 在这个场景里等于没有。
    ///
    /// 【为什么是 7 而不是 3 或者 30】
    /// 3 天撑不住"这周"这个概念——周三是不会知道周末的；
    /// 30 天里绝大多数天是空的，白占 token，还会让模型在很远的未来上纠结。
    /// 7 天正好覆盖"这一周"，也是人排计划时真正会想的跨度。
    /// 更远的以后要做"按需查询"（让 AI 自己开口要某一天），现在先给够一周。
    /// </summary>
    private const int UpcomingDays = 7;

    /// <summary>
    /// 默认往前带几天的回顾。和 UpcomingDays 对称，省得解释"为什么只有未来没有过去"。
    ///
    /// 【为什么回顾不是把界面那 7 天照抄过来】
    /// 界面翻页口径会把所有逾期未完成的旧账并进每一天，逐日拼 7 天就是
    /// 同一条欠账出现 7 次。回顾走 GetDayReviewAsync，只记那天实际发生了什么。
    /// </summary>
    private const int ReviewDays = 7;

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
        PolicyRepository policy,
        ProfileService profile,
        ILogger<AgentService> logger)
    {
        _proxy = proxy;
        _tools = tools;
        _items = items;
        _policy = policy;
        _profile = profile;
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

        // 未来几天。
        //
        // 【为什么一天一天地查，而不是写一个"查某段日期"的新方法】
        // 一天该包含哪些条目，是有讲究的（逾期未完成会自动并进今天、
        // 放弃的不出现、跳过的不算），那套规则只有 GetDayListAsync 里那份是对的。
        // 另写一个范围查询，等于把这条规则抄第二遍——
        // 抄的那份迟早和原版不一致，而这类不一致的表现是"AI 说的和界面上看到的不一样"，
        // 极难查。七天就是七次查询，一个人用的数据量，这个代价换口径唯一，值。
        var upcoming = new List<DayList>();
        for (var i = 1; i <= UpcomingDays; i++)
        {
            var future = day.AddDays(i);
            upcoming.Add(new DayList(future, await _items.GetDayListAsync(future, ct)));
        }

        // 过去几天的回顾。走的是 GetDayReviewAsync（那天实际发生了什么），
        // 不是界面翻页口径——后者会把同一条逾期欠账在 7 天里各带一遍。
        var review = new List<DayList>();
        for (var i = ReviewDays; i >= 1; i--)
        {
            var past = day.AddDays(-i);
            review.Add(new DayList(past, await _items.GetDayReviewAsync(past, ct)));
        }

        // ---------- ② 拼提示 ----------

        var now = DateTime.Now;
        // 先物化一次：下面既要逐条标注，又要从「当前这句之前」算出距上次说话多久。
        var recentHistory = history.Skip(Math.Max(0, history.Count - 20)).ToList();

        var messages = new List<ChatMessage>
        {
            new() { Role = "system", Content = await BuildSystemPrompt(day, items, stats, upcoming, review, floating, stages, ct) }
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
                // 它压根没按格式来，把原话当回复。这种轮次不会有 query。
                return new AgentResult
                {
                    Success = true,
                    Reply = CleanReply(content.Trim()),
                    Outcomes = new List<ActionOutcome>(),
                    BatchId = null,
                    Suggestions = null
                };
            }

            reply = parsed;

            var queries = parsed.Actions.Where(IsQuery).ToList();
            if (queries.Count == 0 || round == MaxQueryRounds)
            {
                break;
            }

            // 【为什么这一轮的其它动作一律不执行】
            // 它同时说「我要查」和「把那条删了」时，那个写动作是基于**还没查到**的信息做的。
            // 先读后写：这一轮只当查询请求，查完让它重新给一次完整的 actions。
            var queryText = await RunQueriesAsync(queries, ct);

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
                Content = "【系统按你的要求查回来的清单】\n" + queryText
                    + "\n\n上面是系统查库的真实结果，不是他说的原话。"
                    + "紧接着那条 assistant 消息只是你刚才申请查询时写的东西，**不算你已经回答过他**。"
                    + "现在给出**最终**答复：仍按原来的 JSON 格式，reply 放你要对他说的那句话，"
                    + "actions 里放这次真正要做的改动（刚才那一轮的其它动作没有执行，要重新给）。"
                    + "不要再发 query。写着「空」、或没列出来的日子，就直说那天没有安排，不要编。"
                    + "不要把查库结果、你的判断或任何格式说明写进 reply。"
            });
            request.Messages = messages;
            request.JsonMode = true;
        }

        // ---------- ④ 执行 ----------
        // query 是只读动作，ItemTools 不认识它，必须在这里剔掉，
        // 否则会多出一条「不认识这个操作」的失败结果显示给他看。
        var actions = reply!.Actions.Where(a => !IsQuery(a)).ToList();
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
    /// 系统提示词。
    ///
    /// 【为什么要在这里写清楚"跳过"和"放弃"的区别】
    /// 模型不知道这两个状态在我们的系统里后果相反（一个明天回来，一个永不再现）。
    /// 不写清楚，它遇到"今天不做"就会随便挑一个，
    /// 用户以为只是今天不做，结果那条任务再也不出现了——
    /// 这是最伤人的一类错误，因为它表现为"软件帮我弄丢了事情"。
    /// </summary>
    private async Task<string> BuildSystemPrompt(
        DateOnly day,
        IReadOnlyList<DayManager.Core.Entities.Item> items,
        ItemRepository.DayStats stats,
        IReadOnlyList<DayList> upcoming,
        IReadOnlyList<DayList> review,
        IReadOnlyList<DayManager.Core.Entities.Item> floating,
        IReadOnlyList<DayManager.Core.Entities.Item> stages,
        CancellationToken ct)
    {
        // 语气是用户自己选的，每次拼提示词时现读——
        // 他在时间页切完档，下一条对话就是新的说法，不需要重启任何东西。
        var tone = AiTone.Parse(await _policy.GetRawAsync(PolicyKeys.AiTone, ct));
        var persona = AiTone.NormalizePersona(await _policy.GetRawAsync(PolicyKeys.AiPersona, ct));

        // 【为什么必须把"现在几点"给它】
        // 它原先只知道今天几号。于是他问"现在几点了""离三点还有多久"，
        // 模型只能瞎编一个钟点——而钟点是这个软件里最不能编的东西之一：
        // 他照着那个错的时间去安排，等发现不对时已经晚了。
        // 反过来，给一个不准的"还有 47 分钟"比不说更糟，所以下面明写"算不准就说大概"。
        var now = DateTime.Now;
        var isToday = day == DateOnly.FromDateTime(now);

        var listText = items.Count == 0
            ? "（今天还没有安排）"
            : string.Join('\n', items.Select(x =>
                $"- id={x.Id} | {x.Title} | {StatusName(x.Status)}" +
                (x.ActualMinutes is > 0 ? $" | 实际 {x.ActualMinutes} 分钟" : "") +
                (x.EstimateMinutes is > 0 ? $" | 预计 {x.EstimateMinutes} 分钟" : "")));

        // 未来几天。
        //
        // 【为什么空的一天也要写出来】
        // "哪天是空的"和"哪天有安排"同样是排期的依据——
        // 不知道哪天空，AI 就会把事情往最早的日子塞，
        // 表现是"每天都排满，而其中三天明明什么都没有"。
        // 空白是一种信息，不是省略的理由。
        //
        // 【为什么要带 id】
        // "把明天的跑步推到后天"这类要求，最终要靠 id 才能执行。
        // 不带 id 的后果很具体：AI 知道有这件事，却改不动它，
        // 于是它只能回一句"我改不了"——而用户会说"你刚才明明看见了"。
        var upcomingText = string.Join('\n', upcoming.Select(x =>
        {
            var head = $"{x.Day:MM-dd}（{x.Day:dddd}）";

            return x.Items.Count == 0
                ? $"{head}：空"
                : head + "\n" + string.Join('\n', x.Items.Select(i =>
                    $"  - id={i.Id} | {i.Title} | {StatusName(i.Status)}" +
                    (i.ActualMinutes is > 0 ? $" | 实际 {i.ActualMinutes} 分钟" : "") +
                    (i.EstimateMinutes is > 0 ? $" | 预计 {i.EstimateMinutes} 分钟" : "")));
        }));

        // 过去几天的回顾：只记那天实际发生过什么，没有逾期旧账。
        // 【为什么不带 id】
        // 回顾是给他「想起那天做了什么」的背景，不是让 AI 去改那些记录——
        // 真要改，得先 query 查到那天的清单（带 id），再按 id 动手。
        // 带了 id 反而会诱它拿一个回顾里的 id 直接改，而回顾口径可能漏掉那天该有的东西。
        var reviewText = string.Join('\n', review.Select(x =>
        {
            var head = $"{x.Day:MM-dd}（{x.Day:dddd}）";

            return x.Items.Count == 0
                ? $"{head}：空"
                : head + "\n" + string.Join('\n', x.Items.Select(i =>
                    $"  - {i.Title} | {StatusName(i.Status)}"));
        }));

        // 【一直悬着、还没到截止日的事】单独一段，不混进今天的清单。
        // 剩余天数由代码算好（ItemPresence.DeadlineLine），不让模型自己减日期——
        // 时间差这类硬数字一律代码给，是已经踩过坑定下的规矩。
        // 带 id：他要是说「报告我提前做完了」，AI 得能直接按 id 标完成。
        var floatingText = floating.Count == 0
            ? "（没有）"
            : string.Join('\n', floating.Select(x =>
                $"- id={x.Id} | {x.Title} | {StatusName(x.Status)} | "
                + (x.DueAt is { } due ? ItemPresence.DeadlineLine(due, day) : "没写截止日")));

        // 阶段性目标：只给名字和归属，不给「还剩几件」——那会诱导模型去催。
        var stagesText = stages.Count == 0
            ? "（没有）"
            : string.Join('\n', stages.Select(x =>
                $"- id={x.Id} | {x.Title}"
                + (x.ParentTaskId is not null ? " | 挂在别的阶段下" : "")
                + (x.DueAt is { } sd ? $" | 阶段截止 {sd:MM-dd}" : "")));

        // 【为什么用逐行拼接而不是一整段原始字符串】
        // 提示词里要给出 JSON 示例，而 JSON 的大括号和 C# 字符串插值的 {{ }} 转义会打架，
        // 一旦括号层数对不上就是编译错误，改起来很烦。
        // 拆成一行一个字符串之后，示例 JSON 就是它本来的样子，不用转义，读起来也对。
        var skippedNote = stats.SkippedToday > 0 ? $"，今天跳过 {stats.SkippedToday} 条" : "";

        // 画像：从最近的行为里算出来的几行事实。
        // 排程时它能让 AI 敢说"你常规只有两小时"这种话——那正是它该做的事。
        var profileText = ProfileLines.ToPrompt(await _profile.BuildAsync(day, ct));

        var lines = new List<string>
        {
            "你是 DayManager 里的日程伙伴。你在和一个**知道自己该做什么、但启动困难**的人说话。",
            "",
            // 只在真的说今天时才给钟点：拼的是别的日子却写"现在 23:14"，
            // 那是拿一个假的时间让他做决定。
            $"【今天】{day:yyyy-MM-dd}（{day:dddd}）" + (isToday ? $"，现在 {now:HH:mm}" : ""),
            $"【今天的清单】共 {stats.Total} 条，已完成 {stats.Done} 条{skippedNote}",
            listText,
            "",
            // 【为什么"实际"要解释一遍】
            // 不解释的话，它容易被读成"这类事就该花这么久"，
            // 于是 AI 拿这个数去填别人的预计——而那一次可能只是他边做边走神。
            // 另外它只在"他按过启动"时才存在，没按过的没有这个数字，不能靠猜补上。
            "清单里「实际」是他按下启动到点完成的时长，只有计过时的才会有；没写的就是没计时，别猜。",
            "拿它估同类的事要留余地：那一次可能中途被打断过，也可能他当时状态特别好。",
            "",
            // 画像和主动开口那边共用同一段（ProfileLines），
            // 两边才会用同一副口径说他。这里额外多一句：
            // 对话里他随时会问"你觉得我怎么样"，那时候才可以说，
            // 平时不许主动报数字——被念出来会变成被盯着。
            profileText,
            $"【过去 {ReviewDays} 天的回顾】只记那天实际发生了什么，不是把界面原样抄来",
            "有具体钟点落在那天的事、以及那天做完的事才在这里；一直欠着没做完的旧账不重复列，它们并进了【今天的清单】。",
            "写着「空」是那天没记录到做过的事，不是没查到。",
            reviewText,
            "",
            $"【接下来 {UpcomingDays} 天已排的】",
            upcomingText,
            "",
            // 【为什么常驻项要单列一段，还先说"不算今天该做完"】
            // 这类事默认只给了一个截止日，中间不属于任何一天。要是混进今天的清单，
            // 模型会把它当成"今天又没做完的一件"去催、去算完成率——
            // 正是 5.2 里「天天出现天天没做，三天后一串欠账」那个反面。
            // 先把身份说死，再讲允许做什么（递小动作），措辞才不会滑成催促。
            "【一直悬着、还没到截止日的事】这些只有截止日，中间哪天做都行，不属于任何一天：",
            "它们**不算今天该做完、不算逾期、不拉低完成率**，别催、别念、别当成欠账。",
            "允许做的只有一件：他明显还没开始时，可以递一个两分钟的小动作（已有的「最小启动步骤」），",
            "给点动力；不要报「还剩几天、压了几件」这种制造压力的话。赦免权照样在。",
            floatingText,
            "",
            // 容器和常驻项一样「不算今天该做完」，但理由不同：
            // 常驻项是没到期限，容器是压根没有完成动作、进度由子项汇总。
            // 不先说清身份，模型会把「第一章」当成今天该做完的一件事去催。
            "【阶段性目标（容器）】这些是装子任务的壳，自己没有完成按钮：",
            "它们**不算今天该做完、不进完成率、别催**；进度由下面挂的子项算出来。",
            "他问「拆到哪了」可以照着说有哪些阶段，别报「还剩几章没做」这种制造压力的话。",
            stagesText,
            "",
            // 【为什么把"你看不到"改成"可以查"】
            // 旧版明写"更早以前的你看不到"，于是用户问昨天的事它照着这句答"看不到昨天的任务"——
            // 那是实话，但用户能翻到任意一天，AI 却说看不到，等于软件里两块视野不一致。
            // 现在默认给足过去+未来，窗口外的让它用 query 自己查。
            // 边界仍然必须写死：不给边界，模型宁可编几条也不肯说"没有"（"我不知道"在它
            // 的训练里是低质量回答），而编造是这个软件里最坏的一类错误——
            // 他会照着不存在的事去安排，然后不再相信这个界面。
            $"【你的视野】默认给你：过去 {ReviewDays} 天的回顾 + 今天 + 接下来 {UpcomingDays} 天 + 上面两段「一直悬着」和「阶段性目标」。",
            "比这更早的日子、或这些天之后的安排，用 query 动作让系统查（见下面【你能做的事】）。",
            "**不要再说「我看不到」**：窗口外的日子是可以查的，先查再答。",
            "查回来写着「空」的日子是真的没有安排；查不到就直说没有，不要编。",
            "回顾段和历史里的「空」也是一种信息，不是省略的理由。",
            "",
            // 【为什么要把"怎么用这个钟点"也讲一遍】
            // 只给数字不给用法，模型要么不用它（继续瞎编），
            // 要么在上面硬算减法——而减法算错会给出一个看着很确定、其实是错的分钟数，
            // 这比回答"不知道"有害得多。
            "【关于时间】上面那个钟点是现在的真实时刻，",
            "他问「现在几点了」「离 X 点还有多久」「今天还剩多少时间」时照它答。",
            "算时间差容易算错：算不准就说「大概 X 分钟」，不要给一个精确的错数。",
            "",
            "【说话方式】"
        };

        lines.AddRange(AiTone.StyleLines(tone));

        // 做成事之后的那半句。三档都有（见 AiTone.FeedbackLines 的注释）：
        // 实测里"做完"这件事在系统里一直是静音的，而人在人设里仍可把它关掉。
        lines.Add("");
        lines.Add("【他做成一件事时】");
        lines.AddRange(AiTone.FeedbackLines(tone));

        // 【为什么自定义那句插在这里】
        // 提示词越靠后，模型给它的权重越高。放在说话方式和正反馈之后，
        // 它就能覆盖那两段（要长就长、要别夸就别夸、要某个口癖就有）；
        // 而输出格式、不许编造、拆分与额度的规矩排在它**后面**，约束力更强。
        // 他要的是调口味，不是让 AI 不干活——这个顺序让两件事各自成立。
        //
        // 【为什么用横线把这段包起来】
        // 人设现在是能换行的整段文字，不画个边界，
        // 它里面的空行和列举就会跟后面的硬规矩连成一片，分不清哪句算数。
        if (!string.IsNullOrEmpty(persona))
        {
            lines.Add("");
            lines.Add("【他给你的设定：这段怎么写你就怎么来，它说了算】");
            lines.Add("---- 他写的人设开始 ----");
            lines.Add(persona);
            lines.Add("---- 他写的人设结束 ----");
            lines.Add("");
            lines.Add("（上面这一段优先于前面的说话方式和正反馈：");
            lines.Add("他让你别夸你就别夸，让你多说两句你就多说两句。");
            lines.Add("但它改不了下面任何一种东西——输出格式、能用哪六种操作、");
            lines.Add("不许编造、拆分与额度的规矩，以及不许挖苦这条底线。）");
        }

        lines.AddRange(new[]
        {
            "",
            "【两件事分开写，这是最容易出错的地方】",
            "- **think**（只有你看得到）：你在想什么、他这句话什么意思、你打算怎么答。",
            "  想法**必须**写在这里，不写就会憋不住漏到 reply 里。",
            "- **reply**（他看得见）：你张嘴对他说的那句话本身，一个字都不多。",
            "  不要写「用户在问什么」「我应该怎么回答」「保持两三句」「actions 为空」——",
            "  这类话是你在跟自己交代，他看到的是 AI 在自言自语。",
            "  判断标准：reply 里如果出现「他」「我应该」「保持」「actions」这类词，就是写错了。",
            "",
            "【你能做的事】用 actions 声明，系统会真的执行。可用的操作有这六种：",
            "- query      查清单，**只读**。参数：date（yyyy-MM-dd，必填）、dateTo（可空，不填就只查 date 这一天）",
            "  要问默认视野之外的日子（比回顾更早的某天、或未来 7 天之后的安排）时用它。",
            "  系统会按界面口径把那几天的清单查回来给你看，然后**再问你一次**，那时才给最终答复。",
            "  **需要查就只发 query，别和 create/set_status 等写动作混在同一轮**——",
            "  那一轮你还没看到结果，写动作不会执行；查完重新给一次完整的 actions 就行。",
            "  一次最多 31 天，查询只能用一次。查不到就直说没有，不要编。",
            "- create     新建一条。参数：title（必填）、date（yyyy-MM-dd，不给就是今天）、time（HH:mm，可空）、dueDate（yyyy-MM-dd，可空）、estimateMinutes（可空）、project（可空）、projectTarget（可空，yyyy-MM-dd）、isStage（可空）、parentTitle（可空）、repeatsDaily（可空）、noVerdict（可空）",
            "  **他说了具体钟点（「下午三点」「明天 9 点半」）时，time 必须填。**",
            "  只把钟点写进 reply、不填 time，建出来的还是一条没有时间的待办——",
            "  他会看到你说「排好了 15:00」，清单里那条却不带钟点。这等于没排。",
            "  **date 和 dueDate 是两回事，二选一，别同时给：**",
            "    date = 「安排在哪一天做」；dueDate = 「只有截止日，中间哪天做都行」。",
            "    他说「12-15 交报告」「下周三之前弄完」「月底前」而**没说哪天动手** → 只给 dueDate，建一条，",
            "    **不要拆、不要摊到各天、不要带 project**。它会一直悬着到那天，但不算今天该做完。",
            "    他说「明天写」「15 号那天做」 → 给 date。给了 dueDate 就别再给 time（那等于又定了具体安排）。",
            "  **阶段性目标（容器）用 isStage=true**：它自己没有完成按钮，进度按下面挂的子项汇总，",
            "  不进「今天该做完」、不算完成率。他说「数学竞赛 → 学第一章 → 做题 / 真题」这类有层次的大事时，",
            "  先建一条 isStage=true 的阶段性目标，再在同一批 actions 里建子项、每条写 parentTitle=那个目标的标题。",
            "  parentTitle 可以再指向另一个阶段性目标，所以能一层层往下挂，不用一次拆到底。",
            "  **别把阶段目标摊成一串平铺的定日待办**：那样既丢了他要的层次，又会让今天的清单一下多出一堆。",
            "  **两种完成方式（只给普通待办，别和 dueDate / isStage 同用）：**",
            "    repeatsDaily=true → 他说「每天背单词」「每天跑 5km」这类**天天各算一次**的事。",
            "      每一次独立结算，漏做只在那天封账、不滚到明天、也不翻旧账问他「昨天怎么没做」。",
            "      **现在还不会自动生成后面几天的实例**：只建这一条，别在 reply 里说「已经排好一周」。",
            "    noVerdict=true → 他说「0 点上床睡觉」「别再熬夜」这类**没有完成动作**的约束。",
            "      不进完成率、不算今天该做完、不滚欠账、不许点名催，也不要递「先做 10 分钟」。",
            "      它不是一类新任务，只是这条记录的待遇；**别在 reply 里给他安一个新类别的名字**。",
            "- set_status 改状态。参数：id（必填）、status（必填）、reason（status=skipped 时写上原因）",
            "- reschedule 改期。参数：id（必填）、date（必填）、time（可空，改几点同样靠它）",
            "- rename     改标题。参数：id（必填）、title（必填）",
            "- delete     删除。参数：id（必填）",
            "",
            "【先判断：这是一件事，还是一条日程】",
            "- **先看有没有「只有截止日」**：他说了期限却没说哪天做（「12-15 交」「月底前」），",
            "  直接建一条 dueDate，下面拆不拆的判据都不用走——它本来就不属于任何一天。",
            "- 判据只有一条：**能不能一次坐下来做完**。",
            "  能——它就是一条日程，直接建，**不要拆、不要带 project**。",
            "  不能——范围含糊、步骤不明、一看就得摊好几天——才轮到拆开再排。",
            "- 一趟就能办完的事，无论听上去多大，都不是大事：",
            "  「下午三点取快递」「今天把作业写了」「看两节网课」「交房租」。",
            "  把这些判成大事再拆成五条，他会看到一堆本来就不需要存在的东西。",
            "- 拿不准就**按不拆**排，然后在 reply 里问一句「要不要拆成几天」。",
            "  这两个方向错起来的代价不对等：他没要求拆你却拆了，他要自己去删；",
            "  你没拆但他想拆，他回一句「拆开」就行。",
            "",
            "【他说不用拆、或者改口说不拆了：先把上一批清干净】",
            "- 你已经按错的方案建过一批，就得把那批删掉（用 delete），再按新要求重建。",
            "- **不要只是「不再提」。**那些东西是真的落在库里了，他在界面上看得见，",
            "  你假装没发生过，他只能自己一条条删。",
            "",
            "【确认要拆的时候，怎么拆】",
            "- 拆成 3~8 条能一次坐下来做完的子任务，每条控制在 25~90 分钟。",
            "  短于 25 分钟做完没有成就感，打勾变成走过场；",
            "  长于 90 分钟的事一看就不想开始——那就等于没排。",
            $"- 按截止日摊到各天，**这件事每天最多 {PerTaskDailyMinutes} 分钟**。",
            "  这个额度是**专门给这一件事的**，不是他一天的总容量——",
            "  他当天原有的安排不占这里的额度，别把它们算进来把这件事压扁。",
            "  （两件事各占两小时，不等于他一天只有两小时。）",
            "  但排之前要看一眼清单里那天已经有什么：加上这批若明显超出他能承受的量，",
            "  就在 reply 里说清楚、给个取舍建议，**不要擅自压缩，也不要替他删掉原有的安排**。",
            "  放不下就砍条数，别硬塞：硬塞的下场是他第一天就做不完，",
            "  然后陷入「计划都做不到 → 挫败 → 干脆放弃」——这正是这个软件要打断的循环。",
            "- 用多条 create 表达，每条带自己的 date。系统会把它们分别放到该在的那天。",
            "- 第一条默认排今天：刚规划完的那一刻启动意愿最强，推到明天等于给了一晚上反悔时间。",
            "",
            "【project 只属于「拆出来的多条」】",
            "- **只有你真的把一个大事拆成了多条，才给这批 create 带上同一个 project。**",
            "  只建一条、或者建几条彼此独立的事，project 一律留空。",
            "- 必须留空的理由很直接：给「取快递」也建一个 project，",
            "  他的大任务页会多出一个只有一条任务的空壳——那不是进度，是噪声。",
            "- project 写这件事本身的名字（比如「开题报告」「数据结构期末复习」），不要写「任务1」这种。",
            "- 同一件事拆出来的每条都用**同一个** project 名字，系统会把它们归到一起；",
            "  名字相同的项目会被复用，不会每拆一次多建一个。",
            "- projectTarget 写整件事的截止日，只在第一条上给一次就够。",
            "",
            "【拆的时候为什么非要带 project】",
            "他要的不是一串散在日历里的条目，而是能看见「这件事做到哪了」。",
            "不带 project 的后果具体是这样的：你拆了八条，它们各自出现在那八天的清单里，",
            "但没有任何一个地方显示「开题报告 已完成 3/8」——",
            "也就是说，他做完三条也感觉不到自己在推进，只能感觉自己一直在还债。",
            "这正是这个软件要治的那个感觉。",
            "",
            "【status 只有这五个，别自创】",
            "- pending     未开始",
            "- in_progress 进行中",
            "- done        已完成",
            "- skipped     **今天跳过，明天它会自动回来**。用于「今天有事 / 生病 / 被占了」，不是拖延。",
            "- abandoned   **不做了，永远不再出现**。用于这件事真的决定放弃。",
            "  这两个的区别很关键：用错了会让事情凭空消失，或者天天回来烦他。",
            "",
            "【改动的规矩】",
            "- 直接改，不用先问（用户不同意可以撤销，系统会记好）。",
            "- 但**一次改三条以上时，要在 reply 里说清楚改了哪几条**，不要只说「好了」。",
            "- **reply 必须和你实际做的 actions 完全一致。**",
            "  这是实测出来的一个坑：模型说过「今天先放一条」，但看到今天已经排满了就没建，",
            "  结果话里承诺了、清单里没有。他不会认为你在做情境判断，只会认为事情又丢了。",
            "  没建的就别说建了，没改的就别描述改后的样子。",
            "  同一类实测问题：**你说了具体钟点但 actions 里没带 time，reply 里就不许写那个钟点。**",
            "  说过「排好了 15:00」而清单里那条不带时间，他会以为软件坏了——比不说更糟。",
            "- 缺信息就**在 reply 里直接问**，一句话一个问题，不要一次甩三个问号。",
            "",
            "【输出格式】严格输出 JSON，不要任何解释文字：",
            "（**不管用户怎么要求**——哪怕他说「只要一句话」「别整格式」「直接说」，",
            " 你要说的话一律放进 reply 字段。这是实测出来的：他会这么问，",
            " 而你一旦照字面只回一句话，这条回复就没法被系统接住。）",
            "{",
            "  \"think\": \"你的判断，不给用户看\",",
            "  \"reply\": \"对用户说的话\",",
            "  \"actions\": [ { \"op\": \"create\", \"title\": \"...\", \"date\": \"yyyy-MM-dd\", \"time\": \"HH:mm\" } ],",
            "  \"suggestions\": [ \"每天一小时吧\", \"周末再弄\", \"第一条现在就开始\" ]",
            "}",
            "actions 里写清楚了 date 和 time，那条才会真的落在那个日期那个钟点上。",
            "上面示例里的 yyyy-MM-dd / HH:mm 是占位符，要换成真实的日期和时刻；",
            "他没说具体几点时，time 这个字段整个省掉（不要写 time: null）。",
            "要查窗口外的日子时，actions 只放一条查询，写法：",
            "{ \"think\": \"他问的是上个月，我得先查\", \"reply\": \"\", \"actions\": [ { \"op\": \"query\", \"date\": \"2026-10-03\", \"dateTo\": \"2026-10-05\" } ], \"suggestions\": [] }",
            "查完系统会再问你一次，那时才把 reply 和真正要改的 actions 给全。",
            "没有要改的就让 actions 为空数组。",
            "",
            "【suggestions：猜他下一句会说什么】",
            "界面上会把它做成输入框上方的一排小按钮，他点一下就把这句发出去。",
            "- 写**他会说的话**，用他的口吻，不要写你要问的问题（\"你打算花多久\"这种不对）。",
            "- 每条是一个能直接发出去的完整短句，最长 15 个字左右。",
            "- 给 2~3 条，最多 4 条。优先放他最可能选的那句（比如你刚给了下一步，就放\"现在开始\"）。",
            "- 你刚问他一个问题的时候，这一条特别有用：把他最可能的回答写出来，他点一下就答了。",
            "- 没什么可猜的（比如他只是在陈述一件事）就不给，留空数组。",
            "",
            "think 是必填的：先想清楚再说话，想到的东西放在那里，别放进 reply。"
        });

        return string.Join('\n', lines);
    }

    private static string StatusName(ItemStatus status) => status switch
    {
        ItemStatus.Pending => "未开始",
        ItemStatus.InProgress => "进行中",
        ItemStatus.Done => "已完成",
        ItemStatus.Skipped => "今天跳过",
        ItemStatus.Abandoned => "不做了",
        _ => status.ToString()
    };

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
    private static bool IsTransientStatus(int statusCode) =>
        statusCode == 429 || (statusCode >= 500 && statusCode != StatusCodes.Status503ServiceUnavailable);

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

    /// <summary>
    /// 某一天的清单，用来喂给模型当上下文。
    ///
    /// 【为什么专门有这个类型】
    /// 提示词里"未来七天"那段既要日期又要条目，
    /// 用一个元组数组也能写，但读的人得回头看元组第二项是什么。
    /// 两个人（人和 AI）都要读这段代码，名字比位置可靠。
    /// </summary>
    private sealed record DayList(DateOnly Day, IReadOnlyList<DayManager.Core.Entities.Item> Items);

}
