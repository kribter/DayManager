using System.Text.Json;
using DayManager.Api.Models;
using DayManager.Core.Ai;
using DayManager.Core.Entities;
using DayManager.Core.Enums;
using DayManager.Core.Intervention;
using DayManager.Core.Proactive;
using DayManager.Core.Profile;
using DayManager.Data;
using DayManager.Data.Repositories;
using Microsoft.EntityFrameworkCore;

// 两个命名空间里都有 ChatMessage：Core 那个是数据库实体（Role/Text），
// Api 这个是发给上游的消息（Role/Content）。这里要的是后者。
using ChatMessage = DayManager.Api.Models.ChatMessage;

namespace DayManager.Api.Services;

/// <summary>AI 主动开口的结果。</summary>
/// <param name="Ok">是否拿到了能说的话。</param>
/// <param name="Title">通知标题，一行、很短。</param>
/// <param name="Text">要说的话。</param>
/// <param name="Error">失败原因。成功时为空。</param>
/// <param name="ItemId">
/// 场合为「娱乐干预」时，模型选中了哪一件事（可以为 null，表示它决定不点名）。
///
/// 【为什么模型只回一个 id，而不是把话说完】
/// 让它自己把标题写进句子里，它就会改写——"12点前躺下来着"这种半句话
/// 听着还行，但列表里那件叫另一个名字时，人会对不上是哪件。
/// 所以它给 id，文案里写占位符，标题由我们按库里的原样替换回去。
/// </param>
public sealed record ProactiveResult(bool Ok, string Title, string Text, string? Error, Guid? ItemId = null)
{
    public static ProactiveResult Fail(string error) => new(false, string.Empty, string.Empty, error);
}

/// <summary>
/// AI 主动开口：**没有用户提问，它自己找话说**。
///
/// 【为什么这一层必须有，不能让客户端自己拼一句话发给 /api/ai/chat】
/// 要说出有价值的话，得先知道今天排了什么、做完了几件、在娱乐上花了多久、
/// 昨天聊过什么。这些东西全在数据库里，而**客户端不应该知道怎么查**
/// ——口径一旦在客户端和服务端各写一份，迟早不一致，
/// 表现是"AI 说的和界面上看到的不是一回事"，极难查。
/// 让后端自己查，客户端只需要说一句"该开口了"。
///
/// 【为什么它不改任何数据】
/// 主动开口本来就招人烦，再让它顺手动手改日程，风险太大：
/// 用户没开口、没确认，第二天发现清单被改过了。
/// 这一版它只说话——想改数据，等用户回一句，走 /api/ai/agent 那条路。
/// </summary>
public sealed class ProactiveService
{
    private readonly AiProxy _proxy;
    private readonly ItemRepository _items;
    private readonly UsageRepository _usage;
    private readonly ChatRepository _chat;
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly PolicyRepository _policy;
    private readonly ProfileService _profile;
    private readonly ILogger<ProactiveService> _logger;

    /// <summary>解析模型输出用的宽松选项：字段名大小写不一致时也能认。</summary>
    private static readonly JsonSerializerOptions ParseOpts = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public ProactiveService(
        AiProxy proxy,
        ItemRepository items,
        UsageRepository usage,
        ChatRepository chat,
        IDbContextFactory<AppDbContext> dbFactory,
        PolicyRepository policy,
        ProfileService profile,
        ILogger<ProactiveService> logger)
    {
        _proxy = proxy;
        _items = items;
        _usage = usage;
        _chat = chat;
        _dbFactory = dbFactory;
        _policy = policy;
        _profile = profile;
        _logger = logger;
    }

    /// <summary>
    /// 生成一句主动要说的话。
    /// </summary>
    /// <param name="kind">场合。</param>
    /// <param name="itemId">场合为「日程前提醒」时那一条的 id。</param>
    /// <param name="today">"今天"是哪天。</param>
    /// <param name="nudge">场合为「娱乐干预」时的现场情况：他在刷什么、刷了多久。</param>
    /// <param name="focus">场合为「专注到点」时的现场情况：他刚做了哪一件、这一轮多长。</param>
    public async Task<ProactiveResult> SpeakAsync(
        ProactiveKind kind,
        Guid? itemId,
        DateOnly today,
        AiOverride? ai = null,
        NudgeRequest? nudge = null,
        FocusFinishRequest? focus = null,
        CancellationToken ct = default)
    {
        // ---------- ① 把"它该知道的"查出来 ----------

        var items = await _items.GetDayListAsync(today, ct);
        var stats = await _items.GetDayStatsAsync(today, ct);
        var usage = await _usage.GetDaySummaryAsync(today, ct);

        // 最近几天：判断"是不是一直很差"要看趋势，不能只看今天。
        var recent = new List<(DateOnly Day, int Total, int Done)>();
        for (var i = 3; i >= 1; i--)
        {
            var d = today.AddDays(-i);
            var s = await _items.GetDayStatsAsync(d, ct);
            recent.Add((d, s.Total, s.Done));
        }

        // 最近聊过什么：不带上它，AI 会把昨天说过的计划再说一遍。
        // "重复劝"正是让人不想打开这个软件的事。
        var history = await _chat.GetRecentAsync(12, ct);

        Item? target = null;
        if (itemId is { } id)
        {
            target = items.FirstOrDefault(x => x.Id == id);
        }

        // "现在几点"要在查数据的同一刻取：隔了几秒再取，
        // 算出来的"还有几分钟"可能和通知弹出的时间对不上。
        var now = DateTimeOffset.Now;

        // ---------- ② 拼提示 ----------

        var messages = new List<ChatMessage>
        {
            new()
            {
                Role = "system",
                Content = await BuildSystemPrompt(
                    kind, today, now, items, stats, usage, recent, target,
                    await _profile.BuildAsync(today, ct),
                    nudge, focus, ct)
            }
        };

        // 物化一次：逐条标注，并从最后一条算出"距上次说话隔了多久"。
        var recentHistory = history.Skip(Math.Max(0, history.Count - 8)).ToList();
        foreach (var m in recentHistory)
        {
            // 历史里只带真正说过的话。系统提示一类的不进对话记录，自然也不会在这里。
            //
            // 【为什么必须标时间】
            // 实体里本来就有 CreatedAt，上一版只取了 m.Text，把它扔了。
            // 于是"18:03 他说去吃饭"和"现在 20:10"之间没有任何距离感，
            // 模型顺着最后一句的语气继续演，两小时后还在劝"饭先吃"。
            // 与聊天链路共用 ChatTime，口径只有一份。
            messages.Add(new ChatMessage
            {
                Role = m.Role == DayManager.Core.Entities.ChatMessage.RoleUser ? "user" : "assistant",
                Content = ChatTime.Mark(m.Text, m.CreatedAt, now.LocalDateTime),
                CreatedAt = m.CreatedAt
            });
        }

        // 和 AgentService 同一段坐标：现在是上午还是深夜、距上次说话多久、
        // 旧状态默认已结束、说话要合时段。主动开口没有"当前这句"，
        // 所以"上一句话"就是历史里最后一条。
        messages.Add(new ChatMessage
        {
            Role = "system",
            Content = ChatTime.Coordinate(now.LocalDateTime, recentHistory.LastOrDefault()?.CreatedAt)
        });

        // 【为什么格式要求要在最后再说一遍】
        // 和 AgentService 里同一个坑：模型对靠后的指令权重最高，
        // 写在系统提示最前面，聊过几轮它的约束力就明显下降。
        messages.Add(new ChatMessage
        {
            Role = "system",
            Content = "输出必须是那个 JSON：think 放你的判断（只有你看得到），"
                + "title 放不超过 8 个字的通知标题，text 放你要对他说的原话。"
                + "不要把想法、格式说明或任何备注写进 text。"
        });

        var request = new ChatRequest
        {
            Messages = messages,
            JsonMode = true,
            Ai = ai,
            // V4 系列会先消耗 reasoning token 再输出内容，给小了的表现是
            // "调用成功但内容是空的"——最难查的那类问题。
            MaxTokens = 2000
        };

        // ---------- ③ 调模型 ----------

        var upstream = await _proxy.ChatAsync(request, ct);

        // 网络闪断、上游 5xx/限流：隔半秒重发一次多半就好。
        if (!upstream.Success && IsTransientStatus(upstream.StatusCode))
        {
            await Task.Delay(500, ct);
            upstream = await _proxy.ChatAsync(request, ct);
        }

        if (!upstream.Success)
        {
            return ProactiveResult.Fail($"调用 AI 失败（{upstream.StatusCode}）");
        }

        var parsed = TryParse(upstream.Body);

        // 空内容重试一次：上游偶尔给回 200 加一个空字符串，
        // 重发一次通常就有内容了。直接放弃的话，用户那边看到的是"AI 什么都没说"。
        if (parsed is null || string.IsNullOrWhiteSpace(parsed.Value.Text))
        {
            _logger.LogWarning("主动开口返回空内容（kind={Kind}），重试一次", kind);
            upstream = await _proxy.ChatAsync(request, ct);
            parsed = TryParse(upstream.Body);
        }

        if (parsed is null || string.IsNullOrWhiteSpace(parsed.Value.Text))
        {
            return ProactiveResult.Fail("AI 这次没说出话来");
        }

        var (title, text, pickedId) = parsed.Value;

        // ---------- ④ 娱乐干预：把占位符换回库里那个原名 ----------

        // 【为什么要替换，而不是让模型直接写句子】
        // 模型写标题会改写：多一个字少一个字、把「交房租」写成「房租」，
        // 人看到通知要愣一下才对上是哪件——这一愣就抵消了"点名"的全部好处。
        // 所以它只负责 Sentence 的语气，标题这个词由我们原样嵌回去。
        // 顺带堵死另一种错：模型有时会挑一件清单外的事，id 对不上就当它没挑。
        Guid? resolvedId = null;
        if (kind == ProactiveKind.Nudge)
        {
            var picked = pickedId is { } pid ? items.FirstOrDefault(x => x.Id == pid) : null;

            if (picked is null)
            {
                // 它用了占位符却给不出能对上的 id：降级成不点名的说法，
                // 而不是让 {任务} 这种东西出现在通知里。
                text = text.Replace(NudgePlaceholder, "那件事");
            }
            else
            {
                text = text.Replace(NudgePlaceholder, picked.Title);
                resolvedId = picked.Id;
            }
        }

        // ---------- ⑤ 晚间总结顺便存进当天上下文 ----------

        // 【为什么要存】
        // 明天的 AI 要能看到"昨晚说过什么"，否则它每天早上重新认识你一遍，
        // 于是每天重新开始劝——重复劝是让人放弃使用的主要原因之一。
        // 这张表和字段早就建好了，一直空着。
        if (kind == ProactiveKind.Evening)
        {
            await SaveDailySummaryAsync(today, text, ct);
        }

        return new ProactiveResult(true, title, text, null, resolvedId);
    }

    /// <summary>
    /// 把晚间总结写进当天上下文。已有记录就覆盖，没有就新建。
    /// </summary>
    private async Task SaveDailySummaryAsync(DateOnly day, string text, CancellationToken ct)
    {
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync(ct);

            var row = await db.DailyContexts.FirstOrDefaultAsync(x => x.Date == day, ct);

            if (row is null)
            {
                row = new DailyContext { Date = day };
                db.DailyContexts.Add(row);
            }

            row.AiDailySummary = text.Length > 500 ? text[..500] : text;
            await db.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            // 存不下不影响这次开口：话说出去了就行，明天看不到总结只是少一点连贯性。
            _logger.LogWarning(ex, "晚间总结没能写进当天上下文");
        }
    }

    /// <summary>从上游响应里取出模型说的话。</summary>
    private static (string Title, string Text, Guid? ItemId)? TryParse(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(body);

            // 上游包一层 choices[0].message.content，content 里才是那个 JSON。
            if (doc.RootElement.TryGetProperty("choices", out var choices) &&
                choices.ValueKind == JsonValueKind.Array &&
                choices.GetArrayLength() > 0)
            {
                var content = choices[0]
                    .GetProperty("message")
                    .GetProperty("content")
                    .GetString();

                if (!string.IsNullOrWhiteSpace(content))
                {
                    var inner = JsonSerializer.Deserialize<ProactiveReply>(content, ParseOpts);
                    if (inner is not null && !string.IsNullOrWhiteSpace(inner.Text))
                    {
                        Guid.TryParse(inner.ItemId, out var picked);
                        return (TrimTitle(inner.Title), inner.Text.Trim(), picked == Guid.Empty ? null : picked);
                    }
                }
            }
        }
        catch (JsonException)
        {
            return null;
        }

        return null;
    }

    /// <summary>
    /// 通知标题截短。
    ///
    /// 【为什么要截】通知标题那一行很窄，超了会被系统截断成"今天…"，
    /// 而标题是唯一能在锁屏/通知中心一眼扫到的东西——截断等于没写。
    /// </summary>
    private static string TrimTitle(string? title)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return "DayManager";
        }

        var t = title.Trim();
        return t.Length > 12 ? t[..12] : t;
    }

    /// <summary>网络闪断、限流这类"再试一次多半就好"的状态。</summary>
    private static bool IsTransientStatus(int statusCode) => AiProxy.IsTransient(statusCode);

    /// <summary>模型按要求返回的 JSON。</summary>
    private sealed class ProactiveReply
    {
        public string? Think { get; set; }
        public string? Title { get; set; }
        public string? Text { get; set; }

        /// <summary>娱乐干预时选中的那一件事的 id。其余场合不返回。</summary>
        public string? ItemId { get; set; }
    }

    /// <summary>模型在文案里指代"它选中那一件事"的占位符。通知发出去之前会被换成真标题。</summary>
    public const string NudgePlaceholder = "{任务}";

    /// <summary>
    /// 娱乐干预那句话的要求。
    ///
    /// 【为什么这几条约束必须写死，而不是"说得有道理就行"】
    /// 干预是唯一一句**在他正做别的事时抢进来**的话。它要短、要具体到一件事、
    /// 不能出现他已经在系统别处看到的数字（通知标题上就是"XX 已经 30 分钟"，
    /// 正文再报一遍等于把同一句话说两遍）。所以这里对篇幅和数字的约束比别的场合都硬。
    ///
    /// 【为什么候选清单在这里单独给一份（带 id）】
    /// 上半段那份清单给的是"今天有什么"，是不带 id 的概览；
    /// 而挑一件点名必须能**指到具体那一条**，所以这里再给一份可引用的候选。
    /// 它同时充当"不许编"的边界：清单外的 id 一律当作没挑。
    /// </summary>
    private static IEnumerable<string> NudgeLines(
        IReadOnlyList<Item> items,
        DateTimeOffset now,
        NudgeRequest? nudge)
    {
        var spent = nudge?.ContinuousMinutes ?? 0;
        var app = string.IsNullOrWhiteSpace(nudge?.AppName) ? "某个应用" : nudge!.AppName;

        yield return "";
        yield return "【这次要说：娱乐超时干预】";
        yield return $"- 他现在正在 {app} 上，**这一段已经连续 {spent} 分钟**（这是刚才连续刷的这一段的时长，不是今天累计）。";
        yield return "- 这句话会在他正刷着的时候「抢」进屏幕。它不是请他聊两句，是要他动起来。";
        yield return "";
        yield return "【你可以点名一件事，也可以不点名】";

        // Actionable：把"现在能动手"的那几件都给它挑，而不是我只替它挑好一件。
        var candidates = NudgeTargetPicker.Actionable(
            items.Where(x => x.Status is ItemStatus.Pending or ItemStatus.InProgress).ToList(),
            now);

        if (candidates.Count == 0)
        {
            // 一件都挑不出来：通常是清单空了，或者剩下的都还没到做的时候
            // （比如排到半夜的那件事在下午被抓出来，就是之前闹过一次笑话的那种）。
            // 这时候把点名这条路明确关掉，比让它自己瞎猜强。
            yield return "- 现在**没有任何一件事是适合马上动手的**（清单可能是空的，";
            yield return "  或者剩下的都还没到做的时候）。这种情况下不要点名任何事，";
            yield return "  说一句让他自己挑一件就行。";
        }
        else
        {
            yield return "- 下面这些事现在能动手。你可以挑一件点名，也可以都不点名：";

            foreach (var c in candidates)
            {
                var hint = c.MinimalStep is { } s && s.Length > 0 ? $"（它的第一步：{s}）" : "";
                yield return $"  - id={c.Id}：{c.Title}{hint}";
            }

            yield return $"- 点名时不要在 text 里写标题本身，写占位符 {NudgePlaceholder}（系统会换成原名）；";
            yield return "  并在 JSON 的 itemId 字段给出那一件的 id。不点名就不要写这个占位符。";
        }

        yield return "";
        yield return "【这一句怎么写】";
        yield return "- 给他一个**小到不需要下决心**的入口。他的问题从来不是不知道该做，是启动不了。";
        yield return "  所以别讲道理、别提「你应该」，直接给那一步做什么。";
        yield return "- **不许出现任何数字**：分钟数、件数、百分比都不要写。";
        yield return "  那些数系统已经替你写在通知标题上了，正文再报一遍只会显得啰嗦，而且容易算错。";
        yield return "- 不许说教，不许翻旧账，不许用「别刷了」「又在玩」这类话开头。";
        yield return "  他被提醒这件事本身已经知道自己在拖延了，你再指出来只是让他把通知划走。";
        yield return "- **默认 40 字以内。**这是抢注意力的一句话，长了他一眼不看就划走。";
        yield return "  例外：他的人设明确要求多说几句时最多 80 字。";
        yield return "- 就算一件都不点名，也要说得像句人话，不要写成「请注意休息」。";
    }

    /// <summary>
    /// 专注计时到点那句话的要求。
    ///
    /// 【为什么这一句最难写】
    /// 它出现在一个微妙的时刻：他刚坐下来做了十分钟，正在决定"要不要继续"。
    /// 写成"还差多少没做完"，这十分钟就成了考核，下次他连开始都不愿意；
    /// 写成"你真棒"，他知道自己只是坐着十分钟，夸得虚。
    /// 所以顺序固定：**先承认已经动了，再给下一步，而且明说"没做完也算"**。
    /// 这个软件要对抗的正是"做不到就干脆别开始"。
    ///
    /// 【为什么不用数字】
    /// 和干预那句同一个规矩：分钟数已经写在通知标题上了，
    /// 正文再报一遍等于把同一句话说两遍，而且容易算错。
    /// </summary>
    private static IEnumerable<string> FocusLines(
        IReadOnlyList<Item> items,
        DateTimeOffset now,
        Item? target,
        FocusFinishRequest? focus)
    {
        var title = focus?.ItemTitle ?? target?.Title ?? string.Empty;
        var minutes = focus?.Minutes ?? 0;
        var step = target?.MinimalStep;

        yield return "";
        yield return "【这次要说：专注到点】";
        yield return minutes > 0
            ? $"- 他刚刚对着「{title}」坐了 {minutes} 分钟，计时器到点了。"
            : $"- 他刚刚在「{title}」上做了一轮，计时器到点了。";

        yield return "";
        yield return "【这一句怎么写】";
        yield return "- **第一句必须是承认已经做了**：他是真的坐下来了，这十分钟是真的。";
        yield return "- 第二句是下一步：做完了就去打个勾，没做完就再来一轮或者先停在这。";
        yield return "- **明说「没做完也算」**：最难的就是开头那一下，开头过了就是过了。";
        yield return "- 不许写成考核：不许出现「还剩」「还没做完」「进度」这类字眼。";
        yield return "- **不许出现任何数字**：分钟数、件数、百分比都不要写，标题上已经有了。";
        yield return "- **默认 40 字以内。** 这是一句收尾的话，不是总结。";
        yield return "  例外：他的人设要求多说几句时最多 80 字。";

        if (step is { Length: > 0 })
        {
            yield return "";
            yield return $"- 如果顺着说下去，可以提到它的第一步是「{step}」。";
        }
    }

    /// <summary>
    /// 按场合拼系统提示。
    ///
    /// 【为什么几种场合共用前半段、只换最后的要求】
    /// 前半段是"你是谁、他是什么状态"，这对四种场合都成立；
    /// 换了只会让模型在早晨和晚上表现得像两个人。
    /// 真正不同的是"这一句要达成什么"，所以只有最后一段分开写。
    /// </summary>
    private async Task<string> BuildSystemPrompt(
        ProactiveKind kind,
        DateOnly today,
        DateTimeOffset now,
        IReadOnlyList<Item> items,
        ItemRepository.DayStats stats,
        DayUsageSummary usage,
        IReadOnlyList<(DateOnly Day, int Total, int Done)> recent,
        Item? target,
        UserProfile profile,
        NudgeRequest? nudge,
        FocusFinishRequest? focus,
        CancellationToken ct)
    {
        // 和对话页共用同一份语气档：两边各写一套的话，
        // 早晨主动说的和他在对话页里问的会像两个人。
        var tone = AiTone.Parse(await _policy.GetRawAsync(PolicyKeys.AiTone, ct));
        var persona = AiTone.NormalizePersona(await _policy.GetRawAsync(PolicyKeys.AiPersona, ct));

        var listText = items.Count == 0
            ? "（今天还没有安排）"
            : string.Join('\n', items.Select(x =>
                $"- {x.Title} | {StatusName(x.Status)}" +
                (x.StartAt is { } st ? $" | {st:HH:mm}" : "") +
                (x.EstimateMinutes is > 0 ? $" | 预计 {x.EstimateMinutes} 分钟" : "")));

        var recentText = string.Join('\n', recent.Select(r =>
            r.Total == 0
                ? $"- {r.Day:MM-dd}：没排事"
                : $"- {r.Day:MM-dd}：{r.Done}/{r.Total}（{(int)((double)r.Done / r.Total * 100)}%）"));

        var lines = new List<string>
        {
            "你是 DayManager 里的日程伙伴。你在和一个**知道自己该做什么、但启动困难**的人说话。",
            "",
            "【这一次的特殊之处】他**没有问你任何问题**——是你主动开口。",
            "这意味着你只有一次机会：他没请求你说话，说得不值他就直接划走，",
            "而且划走两三次之后他会关掉这个功能。所以每一句都必须有具体信息，",
            "不能是「新的一天开始了，加油」这种谁都能说的话。",
            "",
            $"【今天】{today:yyyy-MM-dd}（{today:dddd}），现在 {now:HH:mm}",
            $"【今天的清单】共 {stats.Total} 条，已完成 {stats.Done} 条" +
                (stats.SkippedToday > 0 ? $"，跳过 {stats.SkippedToday} 条" : ""),
            listText,
            "",
            "【他今天的时间账】",
            $"坐在屏幕前 {usage.TotalSeconds / 60} 分钟，其中娱乐 {usage.EntertainmentSeconds / 60} 分钟",
            $"另有一段没开电脑、但他在任务上按了启动的 {usage.WorkSeconds / 60} 分钟",
            $"有记录的时间合计 {usage.AccountedSeconds / 60} 分钟（上面两类重叠的部分只算一次）",
            "",
            "【这几个数是怎么来的——理解错了就会冤枉他】",
            "统计只覆盖两类时间：屏幕前的设备使用，和他自己按下启动到暂停/完成的任务计时。",
            "吃饭、睡觉、出门、走神这些既没开电脑也没计时的时段，不在这几个数里。",
            "所以他很可能有一段时间是在纸上做题、看纸质书、或者出门办事，只是系统看不见。",
            "不要拿娱乐时长去说他「今天都在玩」——那些没被记录的时间里他可能在做事。",
            "要判断这一天过得怎么样，先看上面的清单完成了几条，再看这几个数。",
            "",
            "【最近几天】",
            recentText,
            "",
            ProfileLines.ToPrompt(profile),
            "【说话方式】"
        };

        lines.AddRange(AiTone.StyleLines(tone));

        // 和对话页共用同一份规则（见 AiTone.FeedbackLines）：
        // 晚间总结本来就是最容易写成"数据播报"的一处，这边更要明说。
        lines.Add("");
        lines.Add("【他做成一件事时】");
        lines.AddRange(AiTone.FeedbackLines(tone));

        // 自定义那段插在语气与正反馈之后、下面这几条专属约束之前：
        // 这样它能改说话的分寸和篇幅，但改不动"不要以提问开头"这类规矩
        // ——主动开口本来就不该拿一个空问句开头，这不是口味问题。
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
            lines.Add("但它改不了下面任何一种东西——候选场合要说什么、");
            lines.Add("不许编造，以及不许挖苦这条底线。）");
        }

        lines.AddRange(new[]
        {
            "- 不要以提问开头（「今天过得怎么样？」这种空问句一律不要）。",
            "- 不要道歉、不要说明你是 AI、不要说「我注意到」。",
            "",
            "【绝对不能编】",
            "上面清单里没有的事，就当它不存在。不知道就说不知道。",
            "编一条不存在的事的后果很具体：他照着去做，发现根本没有这件事，",
            "从此不再相信你说的任何一句。"
        });

        // 日程前提醒要把"还有几分钟"替它算好。
        //
        // 【为什么不把两个钟点丢给它让它自己减】
        // 模型做减法是有出错率的，而这是它唯一被允许报出的一个数字——
        // "还有 8 分钟"错一次，他对整套提醒的信任就塌了。
        // 减法是我们能替它做、且一定做对的事，那就别让它做。
        var beforeTimeNote = string.Empty;
        if (kind == ProactiveKind.Before && target?.StartAt is { } startAt)
        {
            var left = (int)Math.Ceiling((startAt - now).TotalMinutes);
            beforeTimeNote = left <= 0
                ? $"- 这一件 {startAt:HH:mm} 开始，已经到点了。"
                : $"- 这一件 {startAt:HH:mm} 开始，还有 {left} 分钟。";
        }

        switch (kind)
        {
            case ProactiveKind.Morning:
                lines.AddRange(new[]
                {
                    "",
                    "【这次要说：早晨计划】",
                    "- 告诉他今天有什么，然后**只挑一件**建议他先动——挑最可能启动的那件，",
                    "  不是最重要那件。他的问题不是不知道轻重，是启动不了。",
                    "- 有开始时间的，把时间带上；没有的，就说明它今天到期。",
                    "- 今天一件都没排时，不要劝他“排点什么”，那是催促。",
                    "  可以说一句今天空的、想做点什么随时叫我。",
                    "- **默认 60 字以内。** 这是推送通知，长了他一眼不看就划走。",
                    "  例外：他的人设里明确要求多说几句时可以放宽，但最多 120 字——再多那是聊天，不是提醒。"
                });
                break;

            case ProactiveKind.Before:
                if (beforeTimeNote.Length > 0)
                {
                    lines.Add("");
                    lines.Add("【这一件的时间】");
                    lines.Add(beforeTimeNote);
                    lines.Add("（这是系统算好的，直接照说，不要自己再算一遍。）");
                }

                lines.AddRange(new[]
                {
                    "",
                    "【这次要说：日程前提醒】",
                    target is null
                        ? "- 马上要开始的那一条没找到（可能刚被改过），就说一句「有件事快到点了，看一眼清单」。"
                        : $"- 点名这一件：**{target.Title}**" +
                          (target.MinimalStep is { } step && step.Length > 0
                              ? $"。它的第一步是「{step}」，直接把这一步说出来——"
                              : "。直接说“现在就做”，然后给一个最小动作——"),
                    "  他要的不是“该开始了”这个通知，而是“第一步做什么”这个答案。",
                    "- **默认 40 字以内。** 这条会弹成系统通知，长了他不看。",
                    "  例外：他的人设要求更长的口吻时最多 80 字，再多就没有“扫一眼”的效果了。"
                });
                break;

            case ProactiveKind.Evening:
                lines.AddRange(new[]
                {
                    "",
                    "【这次要说：晚间总结】**默认控制在 60-80 字**",
                    "- 这是推送，默认篇幅是 UI 决定的，不是内容决定的。",
                    "  例外：他的人设要求写得更多时最多 140 字。这句话同样会被存成今天的总结，越长越没人翻。",
                    "- 结构固定三块：**一句正反馈 + 一个具体的建议 + 一个数据**。",
                    "- 正反馈必须是真的：今天做成了一件事，就说那件事。",
                    "  一件都没做成时，不许硬夸。改成一句不带评价的事实（比如“今天没动”），",
                    "  再加一句明天怎么起步——他最不需要的就是“你很努力了”。",
                    "- 那个数据从上面的统计里取（完成几条、娱乐多少分钟），不要自己编数字。",
                    "- 建议要给到能直接照做的程度：「明天把第一条拆成两半」可以，",
                    "  「明天继续加油」不行。",
                    "- 不许说教，不许翻旧账（“你这周一直没做”这种不要）。",
                    "- 这段话同时会被存成今天的总结，明天你能看到它。一句话，别写成报告。"
                });
                break;

            case ProactiveKind.LowRate:
                lines.AddRange(new[]
                {
                    "",
                    "【这次要说：连续几天完成率很低】",
                    "- **语气要比平时严肃**，但不许挖苦、不许审判、不许说「你又没做」。",
                    "- 先说事实（连续几天做成的事很少），然后**猜一个具体的结构性原因**：",
                    "  是不是排太多了？是不是那件事本身就太大？是不是最近几天根本不在状态？",
                    "- 给一个能立刻做的减法：今天只留一件，或者把最难那条拆一半。",
                    "- 可以问一句“要不要我把今天没做完的重新排一下”，但**不要自己动手改**。",
                    "- **默认 70 字以内。** 严肃的话更忌长，长了就变成说教。",
                    "  例外：他的人设要求更长的口吻时最多 130 字。"
                });
                break;

            case ProactiveKind.Nudge:
                lines.AddRange(NudgeLines(items, now, nudge));
                break;

            case ProactiveKind.Focus:
                lines.AddRange(FocusLines(items, now, target, focus));
                break;
        }

        lines.AddRange(new[]
        {
            "",
            "【输出格式】严格输出 JSON，不要任何解释文字：",
            "{",
            "  \"think\": \"你的判断，不给用户看\",",
            "  \"title\": \"不超过 8 个字的通知标题\",",
            "  \"text\": \"你要对他说的原话\"",
            "}",
            "",
            "text 里只放你张嘴说的那句话本身。",
            "判断标准：如果 text 里出现「他」「我应该」「保持」「用户」这类词，就是写错了。"
        });

        return string.Join('\n', lines);
    }

    private static string StatusName(ItemStatus status) => status.ToDisplayName();
}
