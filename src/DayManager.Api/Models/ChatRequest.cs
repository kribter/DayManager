using System.Text.Json.Serialization;

namespace DayManager.Api.Models;

/// <summary>
/// 客户端发过来的聊天请求。
///
/// 【为什么只定义这几个字段，而不是把上游的完整协议照搬过来】
/// 照搬意味着上游改协议时我们的客户端也要跟着改。
/// 这里只暴露我们真正会用到的能力，将来换供应商，
/// 改动被限制在服务端的转发层，客户端代码一行都不用动。
/// 这层薄薄的抽象叫"防腐层"——防止外部协议的变化腐蚀到自己的代码里。
/// </summary>
public sealed class ChatRequest
{
    /// <summary>完整的对话历史，按时间顺序排列。</summary>
    public List<ChatMessage>? Messages { get; set; }

    /// <summary>
    /// 温度：控制回复的随机性。0 最确定，2 最发散。
    /// 可空是因为不传时让上游用默认值，不要我们替它决定。
    /// </summary>
    public double? Temperature { get; set; }

    /// <summary>本次回复的最大 token 数。</summary>
    public int? MaxTokens { get; set; }

    /// <summary>
    /// 是否要求模型返回严格的 JSON。
    ///
    /// 【为什么单独做成开关】
    /// 后面 AI 要返回结构化的拆解结果（一组子任务），
    /// 如果让它自由发挥，返回来的是"好的，这是我的建议：……"外加一段 JSON，
    /// 客户端根本没法可靠地解析。
    /// 上游提供的 json_object 模式能从机制上保证输出一定是合法 JSON，
    /// 这比在提示词里恳求它"请只返回 JSON"可靠得多。
    /// </summary>
    public bool JsonMode { get; set; }
}

public sealed class ChatMessage
{
    /// <summary>
    /// 角色：system / user / assistant。
    /// 用字符串而不是枚举，是为了容忍上游以后新增角色（比如 tool），
    /// 枚举遇到未知值会直接抛异常，字符串不会。
    /// </summary>
    public string Role { get; set; } = "user";

    public string Content { get; set; } = string.Empty;
}

/// <summary>
/// 转发结果。
///
/// 【为什么返回原始的 JSON 字符串，而不是解析成强类型对象】
/// 上游的响应结构字段很多，我们只用得到其中一小部分，
/// 而且不同供应商的字段还有细微差别。
/// 定义一套完整的强类型结构去映射它，等于把上游的变化引入自己的代码。
/// 直接透传原始 JSON，客户端按需取用，改动面最小。
/// </summary>
public sealed class AiProxyResult
{
    [JsonIgnore]
    public bool Success { get; private init; }

    [JsonIgnore]
    public int StatusCode { get; private init; }

    public string Body { get; private init; } = string.Empty;

    public static AiProxyResult Ok(string body) => new() { Success = true, StatusCode = 200, Body = body };

    public static AiProxyResult Fail(int statusCode, string body) => new() { Success = false, StatusCode = statusCode, Body = body };
}
