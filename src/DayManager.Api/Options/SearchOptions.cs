namespace DayManager.Api.Options;

/// <summary>
/// 联网搜索的配置，对应 appsettings.json 里的 "Search" 一节。
///
/// 【为什么搜索单独一套配置，而不是塞进 Ai】
/// 搜索和聊天是两家不同的服务（聊天是 DeepSeek，搜索是博查/Tavily），
/// 各自的 key、地址、超时都不一样。混在一起会让「换供应商」这件事
/// 变成改一串不知道哪个字段属于哪家的配置。
/// </summary>
public sealed class SearchOptions
{
    public const string SectionName = "Search";

    /// <summary>搜索源：bocha（默认，中文第一）或 tavily。决定请求体格式和返回解析。</summary>
    public string Provider { get; set; } = "bocha";

    /// <summary>例如 https://api.bochaai.com</summary>
    public string BaseUrl { get; set; } = "https://api.bochaai.com";

    /// <summary>搜索接口路径。</summary>
    public string SearchPath { get; set; } = "/v1/web-search";

    /// <summary>
    /// API 密钥。只能来自 appsettings.Local.json 或环境变量，
    /// 任何情况下都不应出现在客户端代码、日志或返回给客户端的响应里。
    /// </summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>请求超时（秒）。搜索通常 2~5 秒，给 15 秒够宽松。</summary>
    public int TimeoutSeconds { get; set; } = 15;
}
