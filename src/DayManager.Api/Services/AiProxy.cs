using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using DayManager.Api.Models;
using DayManager.Api.Options;
using Microsoft.Extensions.Options;

namespace DayManager.Api.Services;

/// <summary>
/// 把客户端的聊天请求转发给真正的 AI 供应商。
///
/// 【这个类是整个后端的核心，也是它存在的唯一理由】
/// API key 从头到尾只在这个文件里被读取，
/// 既不出现在客户端，也不出现在日志里，更不会出现在任何响应里。
/// </summary>
public sealed class AiProxy
{
    // 只序列化我们显式拼出来的字段。
    // 用 Dictionary 而不是定义一个请求类，是因为不同模式下要传的字段不一样
    // （json_object 模式要多传一个 response_format），
    // 用字典可以很自然地"有才加"，不用为了可选字段造一堆可空属性。
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IOptionsMonitor<AiOptions> _options;
    private readonly ILogger<AiProxy> _logger;

    public AiProxy(
        IHttpClientFactory httpClientFactory,
        IOptionsMonitor<AiOptions> options,
        ILogger<AiProxy> logger)
    {
        _httpClientFactory = httpClientFactory;
        _options = options;
        _logger = logger;
    }

    public async Task<AiProxyResult> ChatAsync(ChatRequest request, CancellationToken cancellationToken)
    {
        // 【为什么用 CurrentValue 而不是注入一次就存下来】
        // IOptionsMonitor 每次读取都会拿到最新值，并且感知配置文件变化。
        // 这意味着改了 appsettings.Local.json 不用重启服务就能生效——
        // 在调试 AI 提示词的时候，这个差别是"每次都重启一遍"和"改完直接重试"的区别。
        var opt = _options.CurrentValue;

        // 请求里带了密钥/地址/模型就用请求里的，没带才用服务端配置的那份。
        // 【为什么要有这条"外交部级"的覆盖】
        // 服务端配置是"一个人在自己机器上跑"的用法；把软件发给别人时，
        // 别人的密钥不能（也不该）写进服务端文件——那样分发包里就带着我的凭据。
        // 所以密钥由使用者在自己的界面里填，随请求带来，这里取出来用一次。
        // 它不会被写进任何文件、不会进日志、不会出现在响应里。
        var apiKey = string.IsNullOrWhiteSpace(request.Ai?.ApiKey) ? opt.ApiKey : request.Ai.ApiKey;
        var baseUrl = string.IsNullOrWhiteSpace(request.Ai?.BaseUrl) ? opt.BaseUrl : request.Ai.BaseUrl;
        var model = string.IsNullOrWhiteSpace(request.Ai?.Model) ? opt.Model : request.Ai.Model;

        if (string.IsNullOrWhiteSpace(apiKey))
        {
            // 明确告诉使用者去哪里配，而不是抛一个看不懂的异常。
            // 这里的 503（服务不可用）比 500 准确：不是程序出错，是还没配好。
            //
            // 【为什么提示语改成"去界面里填"】
            // 服务端文件里不再预置任何密钥（否则打包分发出去等于送人额度），
            // 所以"服务端尚未配置"这句话对使用者已经没有行动指引了——
            // 他唯一该去的入口是时间页那张卡。
            return AiProxyResult.Fail(
                StatusCodes.Status503ServiceUnavailable,
                "{\"error\":\"还没有 AI 密钥。到时间页的「AI 接入」里填上你自己的密钥，或者让服务端在 appsettings.Local.json 配 Ai:ApiKey。\"}");
        }

        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            return AiProxyResult.Fail(
                StatusCodes.Status503ServiceUnavailable,
                "{\"error\":\"还没有 AI 接口地址。默认用 DeepSeek，可以在时间页的「AI 接入」里改。\"}");
        }

        // ---------- 拼上游请求体 ----------

        var payload = new Dictionary<string, object?>
        {
            ["model"] = model,
            ["messages"] = (request.Messages ?? new List<ChatMessage>())
                .Select(m => new { role = m.Role, content = m.Content })
                .ToArray(),
            ["stream"] = false
        };

        // 可选字段：只有客户端传了才放进请求体，
        // 让上游用默认值，而不是我们替它编一个。
        if (request.Temperature.HasValue)
        {
            payload["temperature"] = request.Temperature.Value;
        }

        if (request.MaxTokens.HasValue)
        {
            payload["max_tokens"] = request.MaxTokens.Value;
        }

        if (request.JsonMode)
        {
            payload["response_format"] = new { type = "json_object" };
        }

        // ---------- 发出请求 ----------

        // 具名客户端，超时配置来自配置文件。
        var client = _httpClientFactory.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(opt.TimeoutSeconds <= 0 ? 180 : opt.TimeoutSeconds);

        var url = baseUrl.TrimEnd('/') + "/" + opt.ChatPath.TrimStart('/');

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = JsonContent.Create(payload, options: JsonOpts)
        };

        // 密钥唯一的出场位置。注意不要把它塞进任何日志或异常消息里。
        httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

        HttpResponseMessage response;
        try
        {
            response = await client.SendAsync(httpRequest, cancellationToken);
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            // 【为什么要单独区分这个异常】
            // HttpClient 超时抛的也是 TaskCanceledException，
            // 和"客户端主动断开"混在一起。不区分的话，
            // 用户关掉页面产生的取消会被记成超时错误，排查时会被误导。
            // IsCancellationRequested 为 false 说明不是我们主动取消的，那就是真的超时了。
            _logger.LogWarning(ex, "调用 {Provider} 超时（{Timeout} 秒）", opt.Provider, opt.TimeoutSeconds);
            return AiProxyResult.Fail(StatusCodes.Status504GatewayTimeout, "{\"error\":\"调用 AI 超时\"}");
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "调用 {Provider} 时网络错误", opt.Provider);
            return AiProxyResult.Fail(StatusCodes.Status502BadGateway, "{\"error\":\"无法连接 AI 服务\"}");
        }

        using (response)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                // 【这里记录日志时绝对不能记录请求头】
                // 请求头里有 Authorization: Bearer <key>。
                // 只记状态码和响应体——响应体是上游返回的错误描述，不含我们的密钥。
                _logger.LogWarning(
                    "AI 上游返回 {StatusCode}，响应体：{Body}",
                    (int)response.StatusCode,
                    body);

                return AiProxyResult.Fail((int)response.StatusCode, body);
            }

            return AiProxyResult.Ok(body);
        }
    }

    /// <summary>
    /// 这些状态码值得原样重试一次：限流、超时、网关类错误。
    /// 503 排除在外——它在 ChatAsync 里是「没配密钥 / 没配地址」的约定，重试只是白等。
    /// 之前 AgentService 与 ProactiveService 各写一份且判据相反，这里统一。
    /// </summary>
    public static bool IsTransient(int statusCode)
    {
        if (statusCode == StatusCodes.Status408RequestTimeout
            || statusCode == StatusCodes.Status429TooManyRequests)
        {
            return true;
        }

        return statusCode >= 500 && statusCode != StatusCodes.Status503ServiceUnavailable;
    }
}
