namespace DayManager.Api.Options;

/// <summary>
/// 认证相关配置，对应 appsettings.json 里的 "Auth" 一节。
/// </summary>
public sealed class AuthOptions
{
    public const string SectionName = "Auth";

    /// <summary>
    /// 客户端携带 token 时用的请求头名字。
    ///
    /// 【为什么用 header 而不是 URL 参数】
    /// URL 会被原样记进服务器日志、代理日志，甚至浏览器历史；
    /// header 不会。把凭据放在 URL 里是很常见但确实错误的做法，
    /// 错误之处在于泄露面完全不可控——你根本不知道中间有多少环节在记录它。
    ///
    /// 【为什么用 X- 前缀而不是标准的 Authorization】
    /// Authorization 是个有既定语义的头，很多中间件（包括一些反向代理）
    /// 会对它做特殊处理。用一个自定义头可以避免和那些行为打架。
    /// </summary>
    public const string HeaderName = "X-DM-Token";

    /// <summary>固定的共享密钥。留空 = 拒绝一切需要认证的请求。</summary>
    public string Token { get; set; } = string.Empty;
}
