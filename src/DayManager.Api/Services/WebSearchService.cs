using System.Net.Http.Headers;
using System.Net.Http.Json;
using DayManager.Api.Options;
using DayManager.Core.Ai;
using Microsoft.Extensions.Options;

namespace DayManager.Api.Services;

/// <summary>
/// 联网搜索：把一句查询词变成「带出处的事实片段」，回喂给模型。
///
/// 【为什么它返回的是一段文字，而不是结构化对象】
/// 搜索的唯一用途是「模型在拆大事之前想查真实信息」。查回来的东西
/// 下一步就是塞进 prompt 让模型读，所以直接拼成它看得懂的字最省事——
/// 标题 + 网址 + 摘要，网址让它能「据 XX」说、用户也能点开核实。
///
/// 【为什么支持两家搜索源】
/// 默认博查（Bocha）：中文第一，是 DeepSeek 官方搜索源，拆考研/考证这类中文大事最合适。
/// 也留了 Tavily 的旧实现，改配置里的 Provider 就能切回去。
/// 两家都是「POST + Bearer + JSON 请求体」，但请求字段和返回结构不同，所以各写各的。
///
/// 【为什么失败不抛异常】
/// 搜索是加分项，不是地基。没配 key、超时、上游报错，一律返回一句
/// 「为什么没查到」给模型看，让它照自己知道的拆并在 reply 里说明——
/// 绝不能因为一个搜索坏了整轮对话。
/// </summary>
public sealed class WebSearchService
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IOptionsMonitor<SearchOptions> _options;
    private readonly ILogger<WebSearchService> _logger;

    public WebSearchService(
        IHttpClientFactory httpClientFactory,
        IOptionsMonitor<SearchOptions> options,
        ILogger<WebSearchService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _options = options;
        _logger = logger;
    }

    /// <summary>
    /// 搜一次，返回给模型看的一段文字。
    /// </summary>
    /// <param name="keyOverride">
    /// 使用者随请求带来的密钥。传了就用它，没传就回退服务端配置。
    ///
    /// 【为什么优先用请求带来的】
    /// 这个软件要能发给别人用，而搜索 key 是每个人自己的额度。
    /// 服务端配置只作为"开发机上有就顺手用"的兜底——
    /// 分发出去的包里两份配置都空着，谁用谁填。
    /// </param>
    public async Task<string> SearchAsync(string query, string? keyOverride, CancellationToken ct)
    {
        var opt = _options.CurrentValue;

        var key = string.IsNullOrWhiteSpace(keyOverride) ? opt.ApiKey : keyOverride.Trim();

        if (string.IsNullOrWhiteSpace(key))
        {
            return "（联网搜索没配 key，这次没查到。照你自己知道的拆，并在 reply 里说明这一部分可能不全。）";
        }

        var client = _httpClientFactory.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(opt.TimeoutSeconds <= 0 ? 15 : opt.TimeoutSeconds);

        var url = opt.BaseUrl.TrimEnd('/') + "/" + opt.SearchPath.TrimStart('/');

        var payload = BuildPayload(opt.Provider, query);

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = JsonContent.Create(payload)
        };
        httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);

        HttpResponseMessage response;
        try
        {
            response = await client.SendAsync(httpRequest, ct);
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "联网搜索超时（{Timeout} 秒）", opt.TimeoutSeconds);
            return "（联网搜索超时，没查到。照你自己知道的拆，并在 reply 里说明。）";
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "联网搜索网络错误");
            return "（联网搜索连不上，没查到。照你自己知道的拆，并在 reply 里说明。）";
        }

        using (response)
        {
            // 明确按 UTF-8 解码：中文搜索结果全靠它。不依赖上游 Content-Type 里
            // 有没有写 charset（有的供应商会漏，漏了 ReadAsStringAsync 可能按别的编码解）。
            var bodyBytes = await response.Content.ReadAsByteArrayAsync(ct);
            var body = System.Text.Encoding.UTF8.GetString(bodyBytes);

            if (!response.IsSuccessStatusCode)
            {
                // 只记状态码，不记响应体——响应体里可能有搜索词，而搜索词可能含个人信息。
                _logger.LogWarning("搜索上游返回 {StatusCode}", (int)response.StatusCode);
                return "（联网搜索没成功，没查到。照你自己知道的拆，并在 reply 里说明。）";
            }

            return Format(opt.Provider, body);
        }
    }

    private static Dictionary<string, object?> BuildPayload(string provider, string query)
    {
        // 博查的请求体：query + summary（要每条的 AI 摘要）+ count。
        if (string.Equals(provider, "bocha", StringComparison.OrdinalIgnoreCase))
        {
            return new Dictionary<string, object?>
            {
                ["query"] = query,
                ["summary"] = true,
                ["count"] = 5
            };
        }

        // Tavily 的请求体：query + search_depth + include_answer + max_results。
        return new Dictionary<string, object?>
        {
            ["query"] = query,
            ["search_depth"] = "basic",
            ["include_answer"] = true,
            ["max_results"] = 5
        };
    }

    private static string Format(string provider, string body)
    {
        return string.Equals(provider, "bocha", StringComparison.OrdinalIgnoreCase)
            ? SearchResults.FormatBocha(body)
            : SearchResults.Format(body);
    }
}
