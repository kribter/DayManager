namespace DayManager.App.Services;

/// <summary>AI 主动开口的结果。Ok=false 不算错误：没说出话来就安静跳过，别弹打扰。</summary>
public sealed class ProactiveReply
{
    public bool Ok { get; init; }
    public string Title { get; init; } = string.Empty;
    public string Text { get; init; } = string.Empty;
    public string? Error { get; init; }

    /// <summary>场合为 nudge 时 AI 选中了哪一件事（可能为空——它决定不点名）。客户端弹出前再核一次。</summary>
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

    /// <summary>这句话是什么时候说的。历史消息由气泡带出来；无历史的请求留空即可。</summary>
    public DateTimeOffset? CreatedAt { get; set; }

    public static ChatMessageDto System(string content, DateTimeOffset? createdAt = null) => new() { Role = "system", Content = content, CreatedAt = createdAt };
    public static ChatMessageDto User(string content, DateTimeOffset? createdAt = null) => new() { Role = "user", Content = content, CreatedAt = createdAt };
    public static ChatMessageDto Assistant(string content, DateTimeOffset? createdAt = null) => new() { Role = "assistant", Content = content, CreatedAt = createdAt };
}

/// <summary>一轮 AI 对话的结果：它说了什么、动了哪些数据、这一批能不能撤。</summary>
public sealed class AgentTurn
{
    public bool Success { get; set; }
    public string? Error { get; set; }
    public string Reply { get; set; } = string.Empty;

    /// <summary>实际执行的动作。为空说明这一轮只是聊天，没动数据。</summary>
    public List<ActionOutcomeDto> Outcomes { get; set; } = new();

    /// <summary>这一批改动的批次号。有它才能撤销。</summary>
    public Guid? BatchId { get; set; }

    /// <summary>它猜你下一句会说什么。点一下就能发出去，省掉组织一句话。</summary>
    public List<string>? Suggestions { get; set; }

    public bool Ok => Success && string.IsNullOrEmpty(Error);

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

    /// <summary>界面上显示的一行。失败也必须显示，静默跳过会让人以为都成了。</summary>
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

    /// <summary>部分成功也算"成了一些"，必须说清楚冲突，不能简单报成功。</summary>
    public bool Ok => UndoneCount > 0 && !HasConflicts;
}

/// <summary>AI 关于用户的一条记忆（语义条目）。</summary>
public sealed class MemoryDto
{
    public Guid Id { get; set; }
    public string Content { get; set; } = string.Empty;
    public string Reason { get; set; } = string.Empty;
}
