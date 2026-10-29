using System;
using System.Collections.Generic;
using System.Text.Json;
using DayManager.Api.Models;
using DayManager.Core.Ai;
using DayManager.Core.Enums;
using DayManager.Core.Planning;
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

    public AgentService(
        AiProxy proxy,
        ItemTools tools,
        ItemRepository items,
        PolicyRepository policy,
        ILogger<AgentService> logger)
    {
        _proxy = proxy;
        _tools = tools;
        _items = items;
        _policy = policy;
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

        // ---------- ② 拼提示 ----------

        var messages = new List<ChatMessage>
        {
            new() { Role = "system", Content = await BuildSystemPrompt(day, items, stats, upcoming, ct) }
        };

        // 只带最近几轮历史：全部带上的话，聊久了 token 会失控，
        // 而真正影响当前判断的只有最近这几句。
        // （更长的历史压缩进 daily_context 是 M9 的事）
        foreach (var m in history.Skip(Math.Max(0, history.Count - 20)))
        {
            messages.Add(m);
        }

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

        // ---------- ③ 调模型（空内容时最多补两次，最后一次换姿势） ----------

        var upstream = await _proxy.ChatAsync(request, ct);

        // 网络闪断、上游 5xx/限流：隔半秒重发一次多半就好。
        // 直接报错的后果是用户看到"AI 挂了"，其实只是闪了一下。
        if (!upstream.Success && IsTransientStatus(upstream.StatusCode))
        {
            await Task.Delay(500, ct);
            upstream = await _proxy.ChatAsync(request, ct);
        }

        if (!upstream.Success)
        {
            return AgentResult.Failed($"调用 AI 失败：{upstream.Body}");
        }

        var content = ExtractContent(upstream.Body);

        // ---------- ③b 空内容：原样重试一次，再不行就摘掉 json 模式 ----------
        //
        // 【为什么会空】
        // 偶发，且和输入无关——同一句话隔几分钟重发就好（2026-10-27 实测：
        // 用户连着三条得到空回复，同一句话事后原样重放八次全部成功）。
        // 上游不解释原因，只能记下 finish_reason 和 token 用量攒规律
        // （现在落 logs/api-ai.log——控制台随进程消失，App 拉起的后端根本没人看得见）。
        //
        // 【为什么第二次要摘掉 json 模式】
        // 第一次重发赌的是"偶发"；再空，说明这种输入在这种模式下大概率一直空——
        // json_object 配推理模型本就有偶发空输出的毛病。
        // 摘掉之后模型自由发挥：提示词里本来就要求输出 JSON，它多半还是会给；
        // 真给了纯文本，下面的解析兜底也会把原话当回复带出来，比"没接住"强得多。
        //
        // 【为什么不再拿 reasoning_content 顶替】
        // 那是模型对自己说的话，显示出来就是"AI 在自言自语"（D17）。
        // 空就是空，给一句能接住的话，比给人看内心独白强。
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
                return AgentResult.Failed($"调用 AI 失败：{upstream.Body}");
            }

            LogEmpty("三次都空，放弃", upstream, 4);

            // 文案里不提 max_tokens：那只是我们内部的猜测，
            // 而且多数时候不是真相。用户需要的是"下一步该干嘛"，不是一份故障报告。
            return AgentResult.Failed("这一句 AI 没有接住。换个说法再说一次？");
        }

        // ---------- ④ 解析它想做什么 ----------

        AgentReply? reply;
        try
        {
            reply = JsonSerializer.Deserialize<AgentReply>(content, JsonOpts);
        }
        catch (JsonException)
        {
            // 兜底一：它可能前后带了别的话（"好的，结果是：{...}"），
            // 把中间那段 JSON 截出来再试一次。
            reply = TryExtractJson(content);
        }

        // 兜底二：它压根没按格式来。
        //
        // 【为什么这不是故障，而是正常情况】
        // 实测：用户说"你只要回一句话确认收到"，它就真只回了一句话——
        // 纯中文，没有大括号。模型在"听用户的话"和"守输出格式"之间选了前者，
        // 而这个选择是合理的：那种时候本来也没有要执行的动作。
        //
        // 【为什么不报错】
        // 报"AI 返回的不是 JSON"的后果是：明明有内容，界面上却显示一句故障提示，
        // 用户会以为 AI 挂了。把它说的话原样当回复，才是这一轮真实发生的事。
        // 只要它想改数据，就会老老实实给 JSON（那时候 JSON 是它唯一的表达方式）。
        if (reply is null)
        {
            return new AgentResult
            {
                Success = true,
                Reply = CleanReply(content.Trim()),
                Outcomes = new List<ActionOutcome>(),
                BatchId = null,
                Suggestions = null
            };
        }

        // ---------- ⑤ 执行 ----------

        var (outcomes, batchId) = reply.Actions.Count == 0
            ? (new List<ActionOutcome>(), null)
            : await _tools.ExecuteAsync(reply.Actions, Summarize(reply.Actions), ct);

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
        CancellationToken ct)
    {
        // 语气是用户自己选的，每次拼提示词时现读——
        // 他在时间页切完档，下一条对话就是新的说法，不需要重启任何东西。
        var tone = AiTone.Parse(await _policy.GetRawAsync(PolicyKeys.AiTone, ct));
        var persona = (await _policy.GetRawAsync(PolicyKeys.AiPersona, ct))?.Trim();

        var listText = items.Count == 0
            ? "（今天还没有安排）"
            : string.Join('\n', items.Select(x =>
                $"- id={x.Id} | {x.Title} | {StatusName(x.Status)}" +
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
                    (i.EstimateMinutes is > 0 ? $" | 预计 {i.EstimateMinutes} 分钟" : "")));
        }));

        // 【为什么用逐行拼接而不是一整段原始字符串】
        // 提示词里要给出 JSON 示例，而 JSON 的大括号和 C# 字符串插值的 {{ }} 转义会打架，
        // 一旦括号层数对不上就是编译错误，改起来很烦。
        // 拆成一行一个字符串之后，示例 JSON 就是它本来的样子，不用转义，读起来也对。
        var skippedNote = stats.SkippedToday > 0 ? $"，今天跳过 {stats.SkippedToday} 条" : "";

        var lines = new List<string>
        {
            "你是 DayManager 里的日程伙伴。你在和一个**知道自己该做什么、但启动困难**的人说话。",
            "",
            $"【今天】{day:yyyy-MM-dd}（{day:dddd}）",
            $"【今天的清单】共 {stats.Total} 条，已完成 {stats.Done} 条{skippedNote}",
            listText,
            "",
            $"【接下来 {UpcomingDays} 天已排的】",
            // 【为什么必须把"你看不到什么"也写清楚】
            // 不给边界，模型就会补全——问"下个月有什么"，它宁可编几条也不说不知道，
            // 因为"我不知道"在它的训练里是低质量的回答。
            // 而编造是这个软件里最坏的一类错误：用户会照着它说的去安排，
            // 然后发现事情根本不存在，从此不再相信这个界面。
            "这就是你全部的视野：今天，加上这 " + UpcomingDays + " 天。",
            "更早以前的、以及这几天之后的安排，你看不到——不知道就直说不知道，不要编。",
            "空的那几天写着「空」，那是真的没有安排，不是没查到。",
            upcomingText,
            "",
            "【说话方式】"
        };

        lines.AddRange(AiTone.StyleLines(tone));

        // 【为什么自定义那句插在这里，而不是插在最前面】
        // 模型对**靠后**的指令权重更高。放在语气档之后，它能改说话的分寸；
        // 而输出格式、不许编造、不许挖苦这些硬规矩排在它后面，约束力更强。
        // 他要的是调口味，不是让 AI 不干活——这个顺序让两件事各自成立。
        if (!string.IsNullOrEmpty(persona))
        {
            lines.Add("");
            lines.Add("【他自己还写了这句，照着做】");
            lines.Add(persona);
            lines.Add("（这不解除下面任何一条硬规矩。）");
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
            "【你能做的事】用 actions 声明，系统会真的执行。可用的操作只有这五种：",
            "- create     新建一条。参数：title（必填）、date（yyyy-MM-dd，不给就是今天）、time（HH:mm，可空）、estimateMinutes（可空）、project（可空）、projectTarget（可空，yyyy-MM-dd）",
            "- set_status 改状态。参数：id（必填）、status（必填）、reason（status=skipped 时写上原因）",
            "- reschedule 改期。参数：id（必填）、date（必填）、time（可空）",
            "- rename     改标题。参数：id（必填）、title（必填）",
            "- delete     删除。参数：id（必填）",
            "",
            "【他把一件模糊的大事交给你时，要拆开再排】",
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
            "【拆一件事时，必须给这批 create 带上同一个 project】",
            "- project 写这件事本身的名字（比如「开题报告」「数据结构期末复习」），不要写「任务1」这种。",
            "- 同一件事拆出来的每条都用**同一个** project 名字，系统会把它们归到一起；",
            "  名字相同的项目会被复用，不会每拆一次多建一个。",
            "- projectTarget 写整件事的截止日，只在第一条上给一次就够。",
            "",
            "【为什么这条不能省】",
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
            "- 缺信息就**在 reply 里直接问**，一句话一个问题，不要一次甩三个问号。",
            "",
            "【输出格式】严格输出 JSON，不要任何解释文字：",
            "（**不管用户怎么要求**——哪怕他说「只要一句话」「别整格式」「直接说」，",
            " 你要说的话一律放进 reply 字段。这是实测出来的：他会这么问，",
            " 而你一旦照字面只回一句话，这条回复就没法被系统接住。）",
            "{",
            "  \"think\": \"你的判断，不给用户看\",",
            "  \"reply\": \"对用户说的话\",",
            "  \"actions\": [ { \"op\": \"create\", \"title\": \"...\" } ],",
            "  \"suggestions\": [ \"每天一小时吧\", \"周末再弄\", \"第一条现在就开始\" ]",
            "}",
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
