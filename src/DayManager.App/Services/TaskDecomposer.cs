using System.Text.Json;
using DayManager.Core.Enums;
using DayManager.Core.Planning;

namespace DayManager.App.Services;

/// <summary>
/// 让 AI 把一件模糊的大事拆成一串可以动手的子任务。
///
/// 【这个类里真正重要的不是代码，是提示词】
/// 代码只负责把话说出去、把 JSON 收回来。决定效果好坏的是那几段要求写什么，
/// 所以提示词里的每一条规则下面都写了「为什么」，方便以后调整时知道动它会有什么后果。
/// </summary>
public sealed class TaskDecomposer
{
    private readonly ApiClient _api;

    public TaskDecomposer(ApiClient api)
    {
        _api = api;
    }

    // ==================================================================
    // 提示词
    //
    // 【为什么把提示词写成一个常量而不是塞在方法里】
    // 提示词是要反复调的。集中放在文件顶部，改的时候不用在业务逻辑里翻找；
    // 而且下面每条规则都配了"为什么"，改动的人能立刻知道这条是防什么的。
    // ==================================================================

    private const string SystemPrompt = """
        你是任务拆解助手，服务于一个帮助用户对抗拖延的日程软件。
        你的输出会被程序直接解析，所以必须严格返回 JSON，不要有任何多余的话。

        ## 拆解规则

        1. **每条子任务的预计耗时控制在 25 到 90 分钟之间，并且绝对不得超过我在下面给你的「每天可用分钟数」。**
           为什么：短于 25 分钟的事做完了没有成就感，打勾变成走过场；
           长于 90 分钟的事会让人一看就不想开始。
           而超过"每天可用分钟数"的话，这条任务就塞不进任何一天——
           程序不会把它切碎到两天（跨天做的任务要求用户记住昨天做到哪，那本身就是启动摩擦），
           结果就是那一天的总量被它一个人撑爆。
           如果某件事本身就超过这个上限，继续把它拆细，而不是原样保留。

        2. **按顺序排列，把最难、最不确定、或者需要等别人回复的放在前面。**
           为什么：人在有截止日期时会本能地把难的事往后推，推到最后就做不完了。
           把硬骨头放在前面，即使后面进度落后，落下的也是相对容易补的部分。

        3. **每条给一个「最小启动步骤」：两分钟内能做完的第一个动作。**
           为什么：这个软件的用户明确说过，他的瓶颈是"知道该做但启动不了"。
           启动困难的本质是"开始"这个动作的心理成本太高，
           所以要把第一步拆到小得不可能拒绝的程度。
           要求具体到可以直接动手——写"打开文档写下拉标题"，
           不要写"开始写报告"这种等于没说的话。

        4. **判断每条是 must（该做的）还是 want（想做的）。**
           must = 有外部约束，不做会有后果；
           want = 出于兴趣或自我提升，不做也没人追。

        5. **估时宁可偏长，不要偏短——但前提是不超出下面给的「总预算」。**
           为什么：用户明确说过自己常常高估能力、给自己排太多任务，
           结果是完不成 → 挫败 → 干脆放弃。排得乐观会把这个循环放大，
           因为"连计划都做不到"比"我自己没安排好"更打击人。
           宁可让用户第一天轻松完成、后面再追加，也不要第一天就做不完。

           【这条曾经和"这件事每天只有 120 分钟"打架，值得记住】
           只说"宁可偏长"，模型就会把每条都往大了估，于是每天的总量远超用户设定的额度。
           预算是硬约束，偏长只能在预算之内偏：
           预算不够时只能砍条数（在 warnings 里说明砍掉了什么），不能硬塞进去假装排得下。

        6. **控制在 10 条以内。**
           为什么：一屏看不完的计划会让人失去"这件事我能做完"的信心，
           而信心正是启动的前提。
           如果拆出来超过 10 条，说明这件事大到不适合一次排完——
           那就只返回最近这个阶段的子任务，并在 warnings 里说明大概要分几个阶段。
           注意不要为了凑条数把几条合并成超过 90 分钟的大块：
           宁可分阶段，也不要违反第 1 条。

        ## 什么时候必须问而不是猜

        7. **缺了它就不知道该怎么拆的信息，放进 questions 去问，不要自己猜一个默默用。**
           典型的：听众是谁、篇幅多长、交付给谁、有没有前置依赖、预算上限。
           为什么：**缺关键信息时自己猜，等于把一次可能错的猜测伪装成计划。**
           用户看不出来哪里是猜的，一旦发现偏差，整份计划都不可信了。

        8. **问就一定要带一个猜（guess），而且必须是能直接用的具体值。**
           为什么：让人"填"要比让人"改"累得多，尤其是当事主本来就想逃避这件事的时候。
           "听众是谁？"是空的，用户要自己想；
           "听众：同组同事（猜的，你可以改）"只要一眼确认或改两个字。
           猜错了没有代价——它会被用户改掉；不猜的代价是用户干脆不填。

        9. **问题不超过 3 条，只问真正会改变拆解结果的那几条。**
           为什么：问题数量和回答率成反比，问到第 4 条基本就没人搭理了。
           如果确实缺的东西多，选影响最大的三条问，剩下的照最合理的默认拆。

        ## 返回格式

        {
          "understanding": "一句话复述你对这件事的理解",
          "subtasks": [
            { "title": "...", "estimateMinutes": 60, "minimalStep": "...", "kind": "must" }
          ],
          "questions": [
            { "prompt": "听众是谁？", "guess": "同组同事" }
          ],
          "warnings": ["..."]
        }

        - understanding：让用户在两秒内判断"你理解对了没有"。
          这一条是刻意的：一来就问八个问题会把人劝退，
          更好的做法是先说清楚"我是这么理解的"，让用户去改而不是去填。
        - questions：缺了就没法合理拆解的信息。**每条必须给 guess（你的猜测）**，
          用户可以直接采纳你的猜测，也可以改。拿不准的不用问，按最合理的默认拆就行。
        - warnings：如果你觉得这件事大到不可能、或者用户的安排明显不合理，
          把疑问写在这里。注意是提异议并说明理由，不是擅自拒绝——
          有时候是用户低估了难度，有时候这件事确实就是得做。
        """;

    /// <param name="rawInput">用户原始输入的那件事。</param>
    /// <param name="deadline">截止日期，没有就让用户自己估。</param>
    /// <param name="extraInfo">
    /// 用户针对上一轮 questions 补充的信息。第二轮拆解时才会有值。
    ///
    /// 【为什么补信息要带着原始输入一起重发，而不是只发补充的部分】
    /// 模型不记得上一轮聊了什么（每次调用都是独立的）。
    /// 只发"听众是同组同事"这句话，它既不知道原本要做什么，也不知道自己的提问是什么，
    /// 结果一定答非所问。所以每轮都把原始输入 + 这轮的补充一起发，当做一次全新的对话。
    /// </param>
    public async Task<DecomposeResult> DecomposeAsync(
        string rawInput,
        DateTimeOffset? deadline,
        IReadOnlyList<ClarifyingAnswer>? extraInfo = null,
        int dailyMinutes = 0,
        int availableDays = 0,
        string? extraInstruction = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(rawInput))
        {
            return DecomposeResult.Fail("还没有输入要做的事。");
        }

        var now = DateTimeOffset.Now;

        // 把"今天是几号、星期几"一起告诉模型。
        // 【为什么必须显式给它】
        // 模型并不知道今天是什么日子。不给它，遇到"这周五之前""下个月"这类说法，
        // 它只能瞎猜，而且猜错了不会说，只会静默地按一个错误的日期理解。
        // 预算必须算给它看。
        //
        // 【为什么这段是这一版最重要的改动】
        // 原先模型完全不知道"这件事每天只有 120 分钟"——它只知道"估时宁可偏长"。
        // 两条规则一叠加，结果必然是每天的总量远超额度，
        // 用户看到的就成了"我明明设了 120，它给我排出 300 分钟的一天"。
        // 这不是模型不听话，是我们压根没告诉它约束是什么。
        //
        // 【口径】这个额度是**给这一件事的**，不是他一天的总容量。
        // 他当天原有的安排不占这里的额度，别把它们算进来把这件事压扁。
        var budgetLine = string.Empty;
        if (dailyMinutes > 0 && availableDays > 0)
        {
            var budget = dailyMinutes * availableDays;
            budgetLine = $"""

                ## 这件事的时间预算（硬约束）

                - 这件事每天最多：{dailyMinutes} 分钟（这是给它的额度，不是他一天的总余量）
                - 可用天数：{availableDays} 天
                - 总预算：{budget} 分钟

                1. 任何一条子任务的 estimateMinutes 不得超过 {dailyMinutes} 分钟。
                   超过就说明没拆细，必须继续拆，不要原样保留。
                2. 所有子任务的 estimateMinutes 之和不得超过 {budget} 分钟。
                3. 如果这件事确实做不完（需要的分钟数明显大于总预算），
                   只保留最重要的、能塞进预算的部分，
                   并在 warnings 里写清楚：「这件事至少需要多少分钟，
                   建议把截止日推到几号，或者先做哪几条」。
                   不要为了"看起来排得下"而故意低估——低估是这个软件最不能犯的错。
                """;
        }

        var userPrompt = $"""
            今天是 {now:yyyy-MM-dd}（{now:dddd}）。
            {(deadline.HasValue ? $"截止日期：{deadline.Value:yyyy-MM-dd}" : "截止日期：用户没说，你来估一个合理的时间")}。

            要做的事：
            {rawInput.Trim()}
            {budgetLine}
            """;

        // 第二轮才挂上补充信息。
        // 【为什么显式标成"（采纳了你的猜测）"】
        // 用户很可能一个字没改就提交了。不标注的话，模型会以为这是用户明确的新要求，
        // 可能过度解读；标注了它就知道"默认值成立"，照旧执行即可。
        var answered = (extraInfo ?? Array.Empty<ClarifyingAnswer>())
            .Where(x => !string.IsNullOrWhiteSpace(x.Answer))
            .ToList();

        if (answered.Count > 0)
        {
            var lines = answered.Select(x =>
                $"- {x.Prompt} → {x.Answer.Trim()}");

            userPrompt += $"""

                我回答了你上一轮提的问题，请按这些答案重新拆解：
                {string.Join('\n', lines)}
                """;
        }

        // 额外的硬性要求（目前用于"上一次超预算了，这次压到预算内"）。
        //
        // 【为什么这一条要单独传，而不是混进 extraInfo】
        // extraInfo 的语义是"用户回答了你的问题"，而这是"系统要求你改"，
        // 说话的对象不一样。混在一起的话，模型会把它当成用户的补充说明，
        // 可能只是"参考"一下，而这条必须被当作硬约束执行。
        if (!string.IsNullOrWhiteSpace(extraInstruction))
        {
            userPrompt += $"""

                ## 本次额外要求（必须遵守）
                {extraInstruction.Trim()}
                """;
        }

        // max_tokens 给到 8000 是踩过坑之后定的：
        // 一开始给 3000，结果返回的内容在第 97 行被拦腰截断，
        // 解析时报"字符串没有结束"——而且请求本身是成功的，不报任何 AI 相关的错。
        //
        // 原因有两层：
        //   1. DeepSeek V4 会先消耗一大块 token 做推理（响应里有 reasoning_tokens 字段），
        //      这部分也算在 max_tokens 预算里；
        //   2. 拆解结果本身就长——九条子任务，每条带一个写得比较具体的"最小启动步骤"，
        //      实测展开后有九十多行。
        // 宁可多给，也不要卡在临界值上：截断是静默的，排查起来很费劲。
        var reply = await _api.ChatAsync(
            new[]
            {
                ChatMessageDto.System(SystemPrompt),
                ChatMessageDto.User(userPrompt)
            },
            jsonMode: true,
            temperature: 0.3,
            maxTokens: 8000,
            cancellationToken: cancellationToken);

        if (!reply.Success)
        {
            return DecomposeResult.Fail(reply.ErrorMessage ?? "调用 AI 失败。");
        }

        return Parse(reply.Content, reply.TotalTokens);
    }

    // ================= 解析 =================

    /// <summary>
    /// 把模型返回的 JSON 解析成子任务列表。
    ///
    /// 【为什么解析要写得这么宽容】
    /// 模型返回的东西只能"尽量"相信：字段可能缺失、数字可能是字符串、
    /// 外面可能包一层 Markdown 的代码围栏。
    /// 宽容处理的意思是：能用就用，缺了就补默认值，
    /// 只有整体结构完全不对时才算失败。
    /// 严格解析（一处不对就抛异常）会把偶发的格式抖动变成"功能坏了"，
    /// 而用户根本不知道发生了什么。
    /// </summary>
    private static DecomposeResult Parse(string content, int? totalTokens)
    {
        // 去掉可能存在的 Markdown 代码围栏。
        // 模型即使开了 JSON 模式，偶尔还是会在外面包一层 ```json，
        // 直接 JsonDocument.Parse 会失败。剥掉它比要求模型"绝对不要加"更可靠。
        var json = StripCodeFence(content);

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var result = new DecomposeResult
            {
                Success = true,
                TotalTokens = totalTokens,
                Understanding = GetString(root, "understanding")
            };

            if (root.TryGetProperty("subtasks", out var subtasks) && subtasks.ValueKind == JsonValueKind.Array)
            {
                foreach (var node in subtasks.EnumerateArray())
                {
                    var title = GetString(node, "title");
                    if (string.IsNullOrWhiteSpace(title))
                    {
                        continue;
                    }

                    result.Subtasks.Add(new PlannedTask(
                        Title: title,
                        EstimateMinutes: GetInt(node, "estimateMinutes", 60),
                        MinimalStep: GetString(node, "minimalStep"),
                        Kind: string.Equals(GetString(node, "kind"), "want", StringComparison.OrdinalIgnoreCase)
                            ? ItemKind.WantToDo
                            : ItemKind.MustDo));
                }
            }

            if (root.TryGetProperty("questions", out var questions) && questions.ValueKind == JsonValueKind.Array)
            {
                foreach (var node in questions.EnumerateArray())
                {
                    var prompt = GetString(node, "prompt") ?? GetString(node, "question");
                    if (string.IsNullOrWhiteSpace(prompt))
                    {
                        continue;
                    }

                    result.Questions.Add(new ClarifyingQuestion(
                        Prompt: prompt,
                        Guess: GetString(node, "guess")?.Trim() ?? string.Empty));
                }
            }

            if (root.TryGetProperty("warnings", out var warnings) && warnings.ValueKind == JsonValueKind.Array)
            {
                foreach (var node in warnings.EnumerateArray())
                {
                    var text = node.GetString();
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        result.Warnings.Add(text);
                    }
                }
            }

            if (result.Subtasks.Count == 0)
            {
                return DecomposeResult.Fail("AI 没有拆出任何子任务。可以换个说法再试一次。");
            }

            return result;
        }
        catch (JsonException ex)
        {
            // 把原始内容的一小段放进错误信息里。
            // 【为什么】"解析失败"这四个字对排查毫无帮助。
            // 真正需要知道的是：它到底返回了什么——是被截断了、外面包了多余的东西、
            // 还是结构完全不对。给一小段原文，一眼就能分辨。
            var snippet = content.Length > 300 ? content[..300] + " …(已截断)" : content;
            return DecomposeResult.Fail($"AI 返回的内容没法解析成任务列表：{ex.Message}\n原始内容：{snippet}");
        }
    }

    private static string StripCodeFence(string content)
    {
        var text = content.Trim();

        if (!text.StartsWith("```", StringComparison.Ordinal))
        {
            return text;
        }

        var firstNewLine = text.IndexOf('\n');
        if (firstNewLine < 0)
        {
            return text;
        }

        // 去掉开头那一行（可能是 ``` 或 ```json）
        text = text[(firstNewLine + 1)..];

        // 去掉结尾的 ```
        if (text.EndsWith("```", StringComparison.Ordinal))
        {
            text = text[..^3];
        }

        return text.Trim();
    }

    private static string? GetString(JsonElement node, string name)
    {
        return node.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }

    private static int GetInt(JsonElement node, string name, int fallback)
    {
        if (!node.TryGetProperty(name, out var value))
        {
            return fallback;
        }

        // 模型偶尔会把数字写成字符串（"60"），这里两种都能接。
        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetInt32(out var n) => n,
            JsonValueKind.String when int.TryParse(value.GetString(), out var s) => s,
            _ => fallback
        };
    }
}

/// <summary>
/// 拆解结果。
/// </summary>
public sealed class DecomposeResult
{
    public bool Success { get; init; }
    public string? ErrorMessage { get; init; }
    public string? Understanding { get; init; }
    public List<PlannedTask> Subtasks { get; } = new();
    public List<ClarifyingQuestion> Questions { get; } = new();
    public List<string> Warnings { get; } = new();
    public int? TotalTokens { get; init; }

    public static DecomposeResult Fail(string message) => new() { Success = false, ErrorMessage = message };
}

/// <summary>
/// AI 缺的那条关键信息，附带它自己的猜测。
/// </summary>
/// <param name="Prompt">问什么。要短，能放进一个输入框的上方。</param>
/// <param name="Guess">
/// AI 的猜测，会预填进输入框。
///
/// 【为什么 guess 是这个设计里不能省的一半】
/// 让人回答"听众是谁？"需要从零思考；让人接受或改掉"同组同事"只需要一眼。
/// 后者才有可能被真正用起来。就算猜错了也没损失——用户改掉就是。
/// </param>
public sealed record ClarifyingQuestion(string Prompt, string Guess);

/// <summary>
/// 用户对一条 <see cref="ClarifyingQuestion"/> 的回答。
/// </summary>
public sealed record ClarifyingAnswer(string Prompt, string Answer);
