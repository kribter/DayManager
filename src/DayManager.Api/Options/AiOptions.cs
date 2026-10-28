namespace DayManager.Api.Options;

/// <summary>
/// AI 上游服务的配置，对应 appsettings.json 里的 "Ai" 一节。
///
/// 【为什么要抽象出 Provider / BaseUrl / ChatPath 三个字段，而不是写死 DeepSeek】
/// 现在默认用 DeepSeek，但这类服务的价格和效果变化很快，换供应商是大概率事件。
/// 拆成这三个字段之后，换供应商只是改配置，不用改代码——
/// 前提是它兼容 OpenAI 的 /chat/completions 协议（目前主流几家都兼容）。
/// </summary>
public sealed class AiOptions
{
    public const string SectionName = "Ai";

    /// <summary>仅用于日志和排查时知道连的是谁，不参与请求构造。</summary>
    public string Provider { get; set; } = "DeepSeek";

    /// <summary>例如 https://api.deepseek.com</summary>
    public string BaseUrl { get; set; } = string.Empty;

    /// <summary>
    /// 聊天补全的接口路径。
    ///
    /// 【为什么要单独拆出来，而不是直接把完整 URL 写进 BaseUrl】
    /// 因为同一个供应商后面还会加别的接口（比如查余额、列模型）。
    /// 拆开之后复用 BaseUrl 就行，不用把域名复制好几份。
    /// </summary>
    public string ChatPath { get; set; } = "/chat/completions";

    public string Model { get; set; } = string.Empty;

    /// <summary>
    /// API 密钥。
    ///
    /// 【这个字段是整个后端存在的理由】
    /// 它只能来自服务端配置（appsettings.Local.json 或环境变量），
    /// 任何情况下都不应该出现在客户端代码、日志、或者返回给客户端的响应里。
    /// </summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>
    /// 请求超时（秒）。
    ///
    /// 【为什么给到 180 秒这么久】
    /// AI 生成长回复（尤其是后面要做的"拆一整批任务"）本来就要一分多钟。
    /// 默认的 HttpClient 超时是 100 秒，会把正常的慢请求误判成失败，
    /// 表现是"AI 偶尔莫名其妙失败"，非常难查。宁可让它慢，不要让它假失败。
    /// </summary>
    public int TimeoutSeconds { get; set; } = 180;
}
