using System.Text.Json;
using DayManager.Api.Models;
using DayManager.Core.Entities;
using DayManager.Data.Repositories;
using ChatMessage = DayManager.Api.Models.ChatMessage;

namespace DayManager.Api.Services;

/// <summary>
/// 日结时从对话里提炼"关于用户的记忆"。
/// 画像存数字、记忆存话——这是"管家记得你"的落点。
/// </summary>
public sealed class MemoryService
{
    private const int MaxNewMemories = 5;

    private readonly AiProxy _proxy;
    private readonly MemoryRepository _memories;
    private readonly ChatRepository _chat;
    private readonly ILogger<MemoryService> _logger;

    public MemoryService(
        AiProxy proxy,
        MemoryRepository memories,
        ChatRepository chat,
        ILogger<MemoryService> logger)
    {
        _proxy = proxy;
        _memories = memories;
        _chat = chat;
        _logger = logger;
    }

    /// <summary>筛某一天的对话，提炼记忆。返回落了几条。</summary>
    public async Task<int> DistillAsync(DateOnly day, AiOverride? ai = null, CancellationToken ct = default)
    {
        // ① 取那天的对话 + 已有记忆
        var recent = await _chat.GetRecentAsync(100, ct);
        var dayMsgs = recent
            .Where(m => DateOnly.FromDateTime(m.CreatedAt.LocalDateTime) == day)
            .ToList();

        _logger.LogInformation("记忆提炼：{Day}，对话 {Count} 条，密钥={HasAi}",
            day, dayMsgs.Count, ai?.ApiKey is not null);

        if (dayMsgs.Count == 0)
        {
            return 0;
        }

        var existing = await _memories.GetAllAsync(ct);

        // ② 拼提示词，调模型
        var request = new ChatRequest
        {
            Messages = new List<ChatMessage>
            {
                new() { Role = "system", Content = BuildPrompt(dayMsgs, existing) }
            },
            JsonMode = true,
            Ai = ai,
            MaxTokens = 2000
        };

        var upstream = await _proxy.ChatAsync(request, ct);
        if (!upstream.Success)
        {
            _logger.LogWarning("记忆提炼调用失败（{Code}）", upstream.StatusCode);
            return 0;
        }

        // ③ 解析 + 落账
        var extracted = TryParse(upstream.Body);
        if (extracted is null)
        {
            _logger.LogWarning("记忆提炼返回无法解析的 JSON");
            return 0;
        }

        var added = 0;
        foreach (var entry in extracted.Memories ?? new List<MemoryEntry>())
        {
            if (string.IsNullOrWhiteSpace(entry.Content) || added >= MaxNewMemories)
            {
                continue;
            }

            if (!string.IsNullOrWhiteSpace(entry.ReplacesId) && Guid.TryParse(entry.ReplacesId, out var oldId))
            {
                await _memories.UpdateAsync(oldId, entry.Content.Trim(), entry.Reason?.Trim() ?? string.Empty, ct);
            }
            else
            {
                await _memories.AddAsync(entry.Content.Trim(), entry.Reason?.Trim() ?? string.Empty, ct);
            }

            added++;
        }

        return added;
    }

    private static string BuildPrompt(
        IReadOnlyList<DayManager.Core.Entities.ChatMessage> dayMsgs,
        IReadOnlyList<AiMemory> existing)
    {
        var existingText = existing.Count == 0
            ? "（还没有）"
            : string.Join('\n', existing.Select(m => $"- [{m.Id}] {m.Content}（{m.Reason}）"));

        var dialogue = string.Join('\n', dayMsgs.Select(m =>
            $"{(m.Role == DayManager.Core.Entities.ChatMessage.RoleUser ? "他" : "AI")}：{m.Text}"));

        return string.Join('\n', new[]
        {
            "你在为一个人整理「关于他的记忆」。下面是昨天的对话，和他已有的记忆。",
            "",
            "【已有记忆】格式：- [id] 内容（理由）",
            existingText,
            "",
            "【昨天的对话】",
            dialogue,
            "",
            "请从昨天的对话里提炼最多 5 条「以后会用到的、关于他的新事实」。规则：",
            "- 只记关于他的、稳定或重要的、以后对话会用到的：目标、偏好、习惯、重要事件。",
            "- 不记琐碎日常（吃了什么）、一次性任务、寒暄客套。",
            "- 不记关于这个软件本身的使用/测试过程：界面里的具体名字、功能讨论、bug、追问经过——那些是「他和 AI 在调试」，不是「关于他这个人」的事实。",
            "- 用平实的话写内容，别带 AI 腔调（「还挂着」这种口头禅）；主语是「他」，记事实本身。",
            "- 每条必须带「为什么记」的理由（reason）。",
            "- 已经记过的（重复）不要返回；关于同一件事的新事实，用 replacesId 指定要顶掉的旧记忆 id；全新的不填 replacesId。",
            "- 情感类照记，但记事实不记评价（记「他考完自己来报喜」，不记「他很优秀」）。",
            "",
            "严格输出 JSON：",
            "{\"memories\":[{\"content\":\"...\",\"reason\":\"...\",\"replacesId\":\"旧记忆的id或空\"}]}"
        });
    }

    private static MemoryExtraction? TryParse(string body)
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
                    return JsonSerializer.Deserialize<MemoryExtraction>(
                        content, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                }
            }
        }
        catch (JsonException)
        {
            return null;
        }

        return null;
    }
}

/// <summary>模型返回的记忆提炼结果。</summary>
public sealed class MemoryExtraction
{
    public List<MemoryEntry>? Memories { get; set; }
}

/// <summary>一条提炼出的记忆。</summary>
public sealed class MemoryEntry
{
    public string Content { get; set; } = string.Empty;
    public string Reason { get; set; } = string.Empty;

    /// <summary>要顶掉的旧记忆 id。空 = 全新的。</summary>
    public string? ReplacesId { get; set; }
}
