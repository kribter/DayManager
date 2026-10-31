using System.Text.Json;
using DayManager.Api.Models;
using DayManager.Core.Ai;
using DayManager.Core.Entities;
using DayManager.Core.Proactive;
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
public sealed record ProactiveResult(bool Ok, string Title, string Text, string? Error)
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
        ILogger<ProactiveService> logger)
    {
        _proxy = proxy;
        _items = items;
        _usage = usage;
        _chat = chat;
        _dbFactory = dbFactory;
        _policy = policy;
        _logger = logger;
    }

    /// <summary>
    /// 生成一句主动要说的话。
    /// </summary>
    /// <param name="kind">场合。</param>
    /// <param name="itemId">场合为「日程前提醒」时那一条的 id。</param>
    /// <param name="today">"今天"是哪天。</param>
    public async Task<ProactiveResult> SpeakAsync(
        ProactiveKind kind,
        Guid? itemId,
        DateOnly today,
        AiOverride? ai = null,
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
            new() { Role = "system", Content = await BuildSystemPrompt(kind, today, now, items, stats, usage, recent, target, ct) }
        };

        foreach (var m in history.Skip(Math.Max(0, history.Count - 8)))
        {
            // 历史里只带真正说过的话。系统提示一类的不进对话记录，自然也不会在这里。
            messages.Add(new ChatMessage
            {
                Role = m.Role == DayManager.Core.Entities.ChatMessage.RoleUser ? "user" : "assistant",
                Content = m.Text
            });
        }

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

        var (title, text) = parsed.Value;

        // ---------- ④ 晚间总结顺便存进当天上下文 ----------

        // 【为什么要存】
        // 明天的 AI 要能看到"昨晚说过什么"，否则它每天早上重新认识你一遍，
        // 于是每天重新开始劝——重复劝是让人放弃使用的主要原因之一。
        // 这张表和字段早就建好了，一直空着。
        if (kind == ProactiveKind.Evening)
        {
            await SaveDailySummaryAsync(today, text, ct);
        }

        return new ProactiveResult(true, title, text, null);
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
    private static (string Title, string Text)? TryParse(string body)
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
                        return (TrimTitle(inner.Title), inner.Text.Trim());
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
    private static bool IsTransientStatus(int statusCode) =>
        statusCode is 408 or 429 or 500 or 502 or 503 or 504;

    /// <summary>模型按要求返回的 JSON。</summary>
    private sealed class ProactiveReply
    {
        public string? Think { get; set; }
        public string? Title { get; set; }
        public string? Text { get; set; }
    }

    /// <summary>
    /// 按场合拼系统提示。
    ///
    /// 【为什么四种场合共用前半段、只换最后的要求】
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
            "【他今天在电脑/手机上待了多久】",
            $"合计 {usage.TotalSeconds / 60} 分钟，其中娱乐 {usage.EntertainmentSeconds / 60} 分钟",
            "",
            "【最近几天】",
            recentText,
            "",
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

    private static string StatusName(DayManager.Core.Enums.ItemStatus status) => status switch
    {
        DayManager.Core.Enums.ItemStatus.Pending => "未开始",
        DayManager.Core.Enums.ItemStatus.InProgress => "进行中",
        DayManager.Core.Enums.ItemStatus.Done => "已完成",
        DayManager.Core.Enums.ItemStatus.Skipped => "今天跳过",
        DayManager.Core.Enums.ItemStatus.Abandoned => "放弃",
        _ => status.ToString()
    };
}
