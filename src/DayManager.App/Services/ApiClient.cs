using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using DayManager.Core.Proactive;

namespace DayManager.App.Services;

/// <summary>
/// 跟自己的后端说话的客户端。
///
/// 【为什么 AI 请求不能由客户端直连 DeepSeek（这条仍然成立）】
/// 早期设想是把 key 打进客户端：安装包可以被反编译，任何写在里面的字符串
/// ——哪怕加密了，因为解密逻辑也在包里——都等同于公开。所以客户端只跟自己的后端说话。
///
/// 【但"密钥只存在服务端"这条已经不成立了，现在的规矩是这样】
/// 服务端自己的配置文件里现在**不放密钥**（放了就等于随分发包送人）。
/// 使用者在自己的界面里填他自己的那把，存进系统凭据区，
/// 由这里随每次请求带给本机后端；后端取出来用一次，不写盘、不打日志。
/// 要发给别人时，程序目录里干净得像刚出厂。
/// </summary>
public sealed class ApiClient
{
    // 后端要求的认证头名字，必须与服务端 AuthOptions.HeaderName 一致。
    private const string TokenHeader = "X-DM-Token";

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly HttpClient _http;
    private readonly ServerSettings _settings;
    private readonly AiSettings _ai;

    public ApiClient(HttpClient http, ServerSettings settings, AiSettings ai)
    {
        _http = http;
        _settings = settings;
        _ai = ai;
    }

    /// <summary>
    /// 每次请求带上使用者自己的密钥（没填就带 null，让服务端用它自己那份配置）。
    ///
    /// 【为什么是随请求带而不是"启动时上报一次存服务端"】
    /// 服务端一旦把密钥存下来，它就又回到了"某个文件里躺着一把钥匙"的状态，
    /// 而那个文件下一次打包就会被人拷走。随请求带、用完即弃，
    /// 服务端磁盘上从头到尾没有它。
    ///
    /// 【为什么这样做没有额外风险】
    /// 链路是本机回环（127.0.0.1），不出网卡。
    /// </summary>
    private object? AiPayload() =>
        _ai.HasKey
            ? new
            {
                apiKey = _ai.ApiKey,
                baseUrl = string.IsNullOrWhiteSpace(_ai.BaseUrl) ? null : _ai.BaseUrl,
                model = string.IsNullOrWhiteSpace(_ai.Model) ? null : _ai.Model
            }
            : null;

    /// <summary>
    /// 探测后端在不在，顺便问到 AI 是否已经配好。
    ///
    /// 【为什么启动时就要探测一次】
    /// 否则用户要等到真的去点 AI 功能才发现"服务器没开"，
    /// 那时他已经输入完一整段任务描述了。早一秒知道，少一次挫败。
    /// </summary>
    public async Task<HealthResult> GetHealthAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            // 【为什么探测要用单独的短超时，而不是直接用 HttpClient 上那个长的】
            // HttpClient 的超时是实例级的，为了迁就 AI 那种要跑一两分钟的请求，
            // 它被设成了 200 秒。如果探测也用这个值，服务器没开的时候
            // 界面会卡住三分多钟才有反应——那和崩溃没什么区别。
            // CreateLinkedTokenSource 的做法是：外部取消 或 内部 3 秒到点，哪个先来算哪个。
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(3));

            using var request = new HttpRequestMessage(HttpMethod.Get, BuildUrl("/api/health"));
            using var response = await _http.SendAsync(request, timeoutCts.Token);

            if (!response.IsSuccessStatusCode)
            {
                return HealthResult.Fail($"服务器返回 {(int)response.StatusCode}");
            }

            var json = await response.Content.ReadAsStringAsync(cancellationToken);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            // TryGetProperty 而不是直接索引：后端以后可能调整字段，
            // 直接索引遇到字段不存在会抛异常，把一次探测变成一次崩溃。
            return new HealthResult
            {
                Ok = true,
                AiReady = root.TryGetProperty("aiReady", out var ready) && ready.GetBoolean(),
                Provider = root.TryGetProperty("aiProvider", out var p) ? p.GetString() : null,
                Model = root.TryGetProperty("aiModel", out var m) ? m.GetString() : null
            };
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return HealthResult.Fail(DescribeConnectionError(ex));
        }
        catch (JsonException ex)
        {
            return HealthResult.Fail($"服务器返回的内容不是合法 JSON：{ex.Message}");
        }
    }

    /// <summary>
    /// 发一轮对话给 AI。
    /// </summary>
    /// <param name="jsonMode">
    /// 要求模型只返回严格的 JSON。
    /// 【什么时候用】后面做"任务拆解"时必须开：模型自由发挥的话会返回
    /// "好的，这是我的建议：……"外加一段 JSON，客户端根本没法可靠解析。
    /// 开了这个开关后，输出由上游机制保证是合法 JSON，比在提示词里恳求它可靠得多。
    /// </param>
    /// <param name="maxTokens">
    /// 【这里有个已经踩过的坑】DeepSeek V4 系列会先消耗 token 做推理（响应里有 reasoning_tokens 字段）。
    /// 这个值给小了，token 全被推理吃掉，结果是"HTTP 200 但内容是空的"，既不报错也没有答案。
    /// 所以要求长输出（比如拆解结果）时必须给足，宁可多给也不要卡在临界值。
    /// </param>
    public async Task<AiReply> ChatAsync(
        IReadOnlyList<ChatMessageDto> messages,
        bool jsonMode = false,
        double? temperature = null,
        int? maxTokens = null,
        CancellationToken cancellationToken = default)
    {
        var payload = new ChatRequestDto
        {
            Messages = messages.ToList(),
            Ai = AiPayload(),
            JsonMode = jsonMode,
            Temperature = temperature,
            MaxTokens = maxTokens
        };

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, BuildUrl("/api/ai/chat"))
            {
                Content = JsonContent.Create(payload, options: JsonOpts)
            };

            if (!string.IsNullOrWhiteSpace(_settings.Token))
            {
                request.Headers.Add(TokenHeader, _settings.Token);
            }

            using var response = await _http.SendAsync(request, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                return AiReply.Fail(
                    (int)response.StatusCode,
                    ExplainStatus((int)response.StatusCode, body, _settings.Token));
            }

            return ParseReply(body);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return AiReply.Fail(0, DescribeConnectionError(ex));
        }
    }

    /// <summary>
    /// 跟 AI 聊一轮，并且让它真的动手改数据。
    ///
    /// 【和 ChatAsync 的区别】
    /// ChatAsync 只是"问一句话、拿回一段文字"，AI 说什么都不会影响数据。
    /// 这个接口走的是后端的 /api/ai/agent：AI 除了回话，还可以声明一组动作
    /// （新建 / 改状态 / 改期 / 改名 / 删除），后端真的执行，并且整批留痕可撤。
    /// 这是"AI 能管理软件里所有内容"真正落地的地方。
    ///
    /// 【为什么还是乐观更新，不做事前确认】
    /// 之前定过（需求 D4 相关）：改完直接生效 + 告诉你改了什么 + 随时可撤。
    /// 每改一条都先弹一次确认，用起来会像在填审批单，
    /// 而正在逃避的人最受不了的就是多一道手续。
    /// </summary>
    /// <param name="today">
    /// 你正看着哪一天。AI 眼里的"今天"必须和你屏幕上一致，
    /// 否则会出现"我说明天的事，它去改了今天"这种错位。
    /// </param>
    public async Task<AgentTurn> AgentAsync(
        IReadOnlyList<ChatMessageDto> messages,
        DateOnly? today = null,
        CancellationToken cancellationToken = default)
    {
        var payload = new
        {
            messages = messages.Select(x => new { role = x.Role, content = x.Content }).ToList(),
            today = today?.ToString("yyyy-MM-dd"),
            ai = AiPayload()
        };

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, BuildUrl("/api/ai/agent"))
            {
                Content = JsonContent.Create(payload, options: JsonOpts)
            };

            if (!string.IsNullOrWhiteSpace(_settings.Token))
            {
                request.Headers.Add(TokenHeader, _settings.Token);
            }

            using var response = await _http.SendAsync(request, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                return AgentTurn.Fail(
                    ExplainStatus((int)response.StatusCode, body, _settings.Token));
            }

            return ParseAgentTurn(body);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return AgentTurn.Fail(DescribeConnectionError(ex));
        }
    }

    /// <summary>
    /// 让 AI 主动说一句话。**没有人提问**，是客户端判断"该开口了"之后调的。
    /// </summary>
    /// <param name="kind">场合：morning / before / evening / lowrate / nudge / focus。</param>
    /// <param name="itemId">场合为 before 时，是那一条的 id。</param>
    /// <param name="today">你正看着哪一天。</param>
    /// <param name="nudge">场合为 nudge（娱乐干预）时的现场：在刷什么、这一段刷了多久。</param>
    /// <param name="focus">场合为 focus（专注到点）时的现场：刚做了哪一件、这一轮多长。</param>
    public async Task<ProactiveReply> ProactiveAsync(
        string kind,
        Guid? itemId,
        DateOnly? today,
        NudgeRequest? nudge = null,
        FocusFinishRequest? focus = null,
        CancellationToken cancellationToken = default)
    {
        var payload = new
        {
            kind,
            itemId,
            today = today?.ToString("yyyy-MM-dd"),
            appName = nudge?.AppName,
            continuousMinutes = nudge?.ContinuousMinutes,
            focusTitle = focus?.ItemTitle,
            focusMinutes = focus?.Minutes,
            ai = AiPayload()
        };

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, BuildUrl("/api/ai/proactive"))
            {
                Content = JsonContent.Create(payload, options: JsonOpts)
            };

            if (!string.IsNullOrWhiteSpace(_settings.Token))
            {
                request.Headers.Add(TokenHeader, _settings.Token);
            }

            using var response = await _http.SendAsync(request, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                return ProactiveReply.Fail(ExplainStatus((int)response.StatusCode, body, _settings.Token));
            }

            return ParseProactive(body);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return ProactiveReply.Fail(DescribeConnectionError(ex));
        }
    }

    private static ProactiveReply ParseProactive(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var ok = root.TryGetProperty("ok", out var okNode) && okNode.GetBoolean();

            return new ProactiveReply
            {
                Ok = ok,
                Title = root.TryGetProperty("title", out var t) ? t.GetString() ?? "" : "",
                Text = root.TryGetProperty("text", out var x) ? x.GetString() ?? "" : "",
                ItemId = root.TryGetProperty("itemId", out var p) && p.ValueKind == JsonValueKind.String
                    && Guid.TryParse(p.GetString(), out var picked) ? picked : null,
                Error = root.TryGetProperty("error", out var e) ? e.GetString() : null
            };
        }
        catch (JsonException ex)
        {
            return ProactiveReply.Fail($"服务器返回的内容不是合法 JSON：{ex.Message}");
        }
    }

    /// <summary>
    /// 最近有哪些 AI 的改动可以撤销。
    /// </summary>
    public async Task<List<UndoBatchDto>> GetUndoableAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, BuildUrl("/api/undo"));
            if (!string.IsNullOrWhiteSpace(_settings.Token))
            {
                request.Headers.Add(TokenHeader, _settings.Token);
            }

            using var response = await _http.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return new List<UndoBatchDto>();
            }

            var json = await response.Content.ReadAsStringAsync(cancellationToken);
            var list = JsonSerializer.Deserialize<List<UndoBatchDto>>(json, ReadOpts);
            return list ?? new List<UndoBatchDto>();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            // 撤销列表拉不到，不该让整个对话页挂掉——它只是角落里的一个入口。
            return new List<UndoBatchDto>();
        }
    }

    /// <summary>
    /// 撤销 AI 的某一批改动。
    /// </summary>
    /// <param name="force">
    /// 冲突时是否强行覆盖。默认不强行：
    /// 目标在这之后又被改过时，先告诉用户，让他自己决定，而不是默默抹掉后来的改动。
    /// </param>
    public async Task<UndoOutcome> UndoAsync(Guid batchId, bool force = false,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var url = BuildUrl($"/api/undo/{batchId}") + (force ? "?force=true" : string.Empty);
            using var request = new HttpRequestMessage(HttpMethod.Post, url);
            if (!string.IsNullOrWhiteSpace(_settings.Token))
            {
                request.Headers.Add(TokenHeader, _settings.Token);
            }

            using var response = await _http.SendAsync(request, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                return new UndoOutcome
                {
                    Message = ExplainStatus((int)response.StatusCode, body, _settings.Token)
                };
            }

            return JsonSerializer.Deserialize<UndoOutcome>(body, ReadOpts) ?? new UndoOutcome();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return new UndoOutcome { Message = DescribeConnectionError(ex) };
        }
    }

    // ================= 内部 =================

    /// <summary>
    /// 读后端响应用的反序列化选项：大小写不敏感。
    ///
    /// 【为什么这一行值得单独强调】
    /// 后端是 C#，序列化出来是 camelCase（batchId）；客户端字段名是 PascalCase（BatchId）。
    /// System.Text.Json 默认**大小写敏感**，配错的后果不是报错，
    /// 而是所有字段静默变成默认值——表现为"撤销成功了但什么都没发生"。
    /// 这个项目已经因为同一类问题踩过一次（token 读成空导致 401）。
    /// </summary>
    private static readonly JsonSerializerOptions ReadOpts = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private static AgentTurn ParseAgentTurn(string body)
    {
        try
        {
            var dto = JsonSerializer.Deserialize<AgentTurn>(body, ReadOpts);
            return dto ?? AgentTurn.Fail("后端返回的内容是空的。");
        }
        catch (JsonException ex)
        {
            return AgentTurn.Fail($"解析后端返回内容失败：{ex.Message}");
        }
    }

    /// <summary>
    /// 每次请求都现拼完整 URL，而不是把基址写进 HttpClient.BaseAddress。
    ///
    /// 【为什么】BaseAddress 是 HttpClient 实例级的，改起来要重建客户端；
    /// 而服务器地址是可以在运行时改的（换成 Tailscale 地址、换成云服务器地址）。
    /// 现拼虽然多几次字符串连接，但省掉了"改地址要重建客户端"这个约束。
    /// </summary>
    private string BuildUrl(string path)
    {
        var baseUrl = _settings.BaseUrl.TrimEnd('/');
        return baseUrl + (path.StartsWith('/') ? path : "/" + path);
    }

    /// <summary>
    /// 把网络层异常翻译成人看得懂的话。
    ///
    /// 【为什么值得为错误写这么多字】
    /// "连不上服务器"和"token 填错了"是两种完全不同的问题，
    /// 但如果都显示成"请求失败"，你就得自己去猜是哪一种。
    /// 排查成本几乎全部花在"到底是哪里错了"上，所以错误信息必须指名道姓。
    /// </summary>
    private string DescribeConnectionError(Exception ex)
    {
        if (ex is TaskCanceledException)
        {
            return $"请求超时。后端地址是 {_settings.BaseUrl}，确认服务在跑，且这个地址能访问。";
        }

        return $"连不上服务器（{_settings.BaseUrl}）。后端没启动，或者地址不对。";
    }

    /// <param name="tokenSent">
    /// 本次请求实际带上的 token。用它区分「根本没配」和「配错了」——
    /// 这两件事界面上都表现为 401，但排查方向完全不同。
    /// </param>
    private static string ExplainStatus(int statusCode, string body, string? tokenSent)
    {
        // 后端对几种情况返回的是明确的中文提示，原样展示比二次包装更准确。
        var serverMessage = TryReadError(body);

        return statusCode switch
        {
            //
            // 【为什么 401 要再分两种说法】
            // token 为空时，上面那段代码压根不会带上认证头，服务端照样返回 401。
            // 如果这时只说"token 不对"，用户会去反复比对两边的值，
            // 而实际问题是**客户端压根没读到配置**（比如种子文件路径不对、字段名大小写不匹配）。
            // "没配"和"配错"的下一步动作完全不同：前者去查配置文件，后者去比对值。
            //
            401 when string.IsNullOrWhiteSpace(tokenSent) =>
                "客户端没有配置 token。检查 F:\\DayManager\\data\\client.json 里有没有 token 字段，"
                + "以及它是否被真正读到了。",

            401 => "token 不对。客户端填的令牌要和后端 appsettings.Local.json 里的 Auth:Token 完全一致，"
                   + "注意别多复制了空格或换行。",

            403 => "没有权限。" + serverMessage,
            503 => "服务器还没配好：" + serverMessage,
            502 or 504 => "后端连 AI 供应商失败：" + serverMessage,
            _ => $"服务器返回 {statusCode}。{serverMessage}"
        };
    }

    private static string TryReadError(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("error", out var err))
            {
                return err.GetString() ?? string.Empty;
            }
        }
        catch (JsonException)
        {
            // 响应体不是 JSON（比如上游直接返回了一段 HTML），
            // 那就没什么可读的，返回空串。
        }

        return string.Empty;
    }

    private static AiReply ParseReply(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;

            if (!root.TryGetProperty("choices", out var choices) || choices.GetArrayLength() == 0)
            {
                return AiReply.Fail(0, "AI 返回的内容里没有 choices 字段，可能响应格式变了。");
            }

            var message = choices[0].GetProperty("message");

            return new AiReply
            {
                Success = true,
                Content = message.GetProperty("content").GetString() ?? string.Empty,
                Model = root.TryGetProperty("model", out var m) ? m.GetString() : null,
                TotalTokens = root.TryGetProperty("usage", out var u)
                    && u.TryGetProperty("total_tokens", out var t)
                        ? t.GetInt32()
                        : null
            };
        }
        catch (JsonException ex)
        {
            return AiReply.Fail(0, $"解析 AI 返回内容失败：{ex.Message}");
        }
    }
}

/// <summary>
/// AI 主动开口的结果。
///
/// 【为什么 Ok=false 不算错误】
/// 用户没有请求这句话，AI 没说出话来（模型抽风、今天没事可说）时，
/// 正确行为是安静跳过——弹出一个"AI 说话失败"给他看，等于凭空制造一次打扰。
/// </summary>
public sealed class ProactiveReply
{
    public bool Ok { get; init; }
    public string Title { get; init; } = string.Empty;
    public string Text { get; init; } = string.Empty;
    public string? Error { get; init; }

    /// <summary>
    /// 场合为 nudge 时，AI 选中了哪一件事（可能为空——它决定不点名）。
    ///
    /// 【为什么客户端要这个 id 而不是只要有句话就行】
    /// 通知是提前备好的，中间隔了快一分钟；到真要弹的那一刻，
    /// 那人可能已经把这件事做完了。拿着 id 才能在弹出前再核一次，
    /// 别谢谢他去"接着做"一件刚刚已经做完的事。
    /// </summary>
    public Guid? ItemId { get; init; }

    public static ProactiveReply Fail(string error) => new() { Ok = false, Error = error };
}

public sealed class HealthResult
{
    public bool Ok { get; init; }
    public bool AiReady { get; init; }
    public string? Provider { get; init; }
    public string? Model { get; init; }
    public string? ErrorMessage { get; init; }

    public static HealthResult Fail(string message) => new() { Ok = false, ErrorMessage = message };
}

public sealed class AiReply
{
    public bool Success { get; init; }
    public string Content { get; init; } = string.Empty;
    public string? Model { get; init; }
    public int? TotalTokens { get; init; }
    public string? ErrorMessage { get; init; }
    public int StatusCode { get; init; }

    public static AiReply Fail(int statusCode, string message) =>
        new() { Success = false, StatusCode = statusCode, ErrorMessage = message };
}

public sealed class ChatRequestDto
{
    public List<ChatMessageDto>? Messages { get; set; }
    public double? Temperature { get; set; }
    public int? MaxTokens { get; set; }
    public bool JsonMode { get; set; }

    /// <summary>本次请求自带的密钥 / 接口地址 / 模型。没填就是 null，服务端用它自己那份。</summary>
    public object? Ai { get; set; }
}

public sealed class ChatMessageDto
{
    public string Role { get; set; } = "user";
    public string Content { get; set; } = string.Empty;

    public static ChatMessageDto System(string content) => new() { Role = "system", Content = content };
    public static ChatMessageDto User(string content) => new() { Role = "user", Content = content };
    public static ChatMessageDto Assistant(string content) => new() { Role = "assistant", Content = content };
}

/// <summary>
/// 一轮 AI 对话的结果：它说了什么、动了哪些数据、这一批能不能撤。
/// </summary>
public sealed class AgentTurn
{
    public bool Success { get; set; }
    public string? Error { get; set; }
    public string Reply { get; set; } = string.Empty;

    /// <summary>实际执行的动作。为空说明这一轮只是聊天，没动数据。</summary>
    public List<ActionOutcomeDto> Outcomes { get; set; } = new();

    /// <summary>这一批改动的批次号。有它才能撤销。</summary>
    public Guid? BatchId { get; set; }

    /// <summary>
    /// 它猜你下一句会说什么。
    ///
    /// 【为什么猜的是"你会说的话"，而不是"它想问的问题"】
    /// 追问用嘴问就够了——话说反而是最省控件的入口。
    /// 这里要省的是另一件事：想一句完整的话并打出来。
    /// 点一下就发出去，对正在犹豫要不要开始的人，
    /// 和"自己组织一句回复"是两种代价。
    /// </summary>
    public List<string>? Suggestions { get; set; }

    public bool Ok => Success && string.IsNullOrEmpty(Error);

    /// <summary>这一轮真的改了数据吗。</summary>
    public bool Changed => Outcomes.Count > 0;

    public bool HasSuggestions => Suggestions is { Count: > 0 };

    public static AgentTurn Fail(string message) => new() { Success = false, Error = message };
}

public sealed class ActionOutcomeDto
{
    public string Op { get; set; } = string.Empty;
    public bool Ok { get; set; }
    public string Detail { get; set; } = string.Empty;
    public Guid? Id { get; set; }
    public string? Title { get; set; }

    /// <summary>
    /// 界面上显示的一行。成功说做了什么，失败说为什么——
    /// 失败也必须显示出来，静默跳过会让人以为都成了。
    /// </summary>
    public string Display => Ok ? Detail : $"没做成：{Detail}";
}


/// <summary>"最近可撤销"列表里的一行。</summary>
public sealed class UndoBatchDto
{
    public Guid BatchId { get; set; }
    public string Summary { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
    public int EntryCount { get; set; }
    public bool IsAi { get; set; }

    /// <summary>服务端算好的一行摘要（"AI · 3 分钟前 · 改了 5 条"）。</summary>
    public string? DisplayText { get; set; }

    public string Text => string.IsNullOrWhiteSpace(DisplayText)
        ? $"{(IsAi ? "AI" : "你")} · {Summary}（{EntryCount} 条）"
        : DisplayText;
}

public sealed class UndoOutcome
{
    public int UndoneCount { get; set; }
    public List<string> Conflicts { get; set; } = new();
    public string Message { get; set; } = string.Empty;

    public bool HasConflicts => Conflicts.Count > 0;

    /// <summary>
    /// 部分成功也算"成了一些"：撤回了 3 条里的 2 条，剩下 1 条有冲突。
    /// 这种情况必须说清楚，不能简单报成功——用户会以为全撤了。
    /// </summary>
    public bool Ok => UndoneCount > 0 && !HasConflicts;
}
