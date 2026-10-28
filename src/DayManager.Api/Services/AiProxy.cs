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

        if (string.IsNullOrWhiteSpace(opt.ApiKey))
        {
            // 明确告诉使用者去哪里配，而不是抛一个看不懂的异常。
            // 这里的 503（服务不可用）比 500 准确：不是程序出错，是还没配好。
            return AiProxyResult.Fail(
                StatusCodes.Status503ServiceUnavailable,
                "{\"error\":\"服务端尚未配置 Ai:ApiKey。请把密钥写进 appsettings.Local.json（该文件已被 git 忽略），不要写进代码或 appsettings.json。\"}");
        }

        if (string.IsNullOrWhiteSpace(opt.BaseUrl))
        {
            return AiProxyResult.Fail(
                StatusCodes.Status503ServiceUnavailable,
                "{\"error\":\"服务端尚未配置 Ai:BaseUrl。\"}");
        }

        // ---------- 拼上游请求体 ----------

        var payload = new Dictionary<string, object?>
        {
            ["model"] = opt.Model,
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

        var url = opt.BaseUrl.TrimEnd('/') + "/" + opt.ChatPath.TrimStart('/');

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = JsonContent.Create(payload, options: JsonOpts)
        };

        // 密钥唯一的出场位置。
        httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", opt.ApiKey);

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
}
