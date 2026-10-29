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

    /// <summary>
    /// 本次请求自带的密钥 / 接口地址 / 模型。
    ///
    /// 【为什么允许请求临时带密钥，而不是只从服务端文件读】
    /// 这个软件是要能发给别人用的。密钥如果只能写进服务端的配置文件，
    /// 那么分发包里就必须带着我自己的那把——别人拿来直接烧我的额度。
    /// 让每个使用者在自己的界面里填自己的密钥，这次请求带上、用完即弃，
    /// 服务端不留一行，才是唯一能"发出去而不送凭据"的做法。
    ///
    /// 【为什么是"带了就用，没带就用服务端那份"】
    /// 服务端那份是给"一个人在自己机器上跑"这种场景留的兜底：
    /// 配了就能用，不用先去界面填一遍。两者不冲突，优先级是请求 > 文件。
    ///
    /// 【安全性边界】
    /// 这条链路是本机回环（127.0.0.1），密钥不过公网；
    /// 后端不写盘、不打日志、不出现在任何响应里。
    /// </summary>
    public AiOverride? Ai { get; set; }
}

/// <summary>
/// 一次请求里对 AI 连接的临时覆盖。三项都可空：不写就用服务端配置的那份。
/// </summary>
public sealed class AiOverride
{
    public string? ApiKey { get; set; }
    public string? BaseUrl { get; set; }
    public string? Model { get; set; }
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
