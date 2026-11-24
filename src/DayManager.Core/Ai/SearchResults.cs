using System.Text.Json;

namespace DayManager.Core.Ai;

/// <summary>
/// 把联网搜索返回的 JSON 拼成给模型看的一段文字。
///
/// 【为什么放在 Core 而不是 Api】
/// 这是纯函数（进 JSON、出文字），不碰网络、不碰配置，
/// 正好符合 Core 的规矩「零依赖的纯逻辑放这里」。放这里的好处是冒烟测试
/// 能直接喂一段 JSON 验证它，不用真的去调一次搜索接口。
/// </summary>
public static class SearchResults
{
    /// <summary>
    /// 把 Tavily 格式的响应体拼成模型看得懂的文字：综合回答 + 每条结果的标题/网址/摘要。
    /// 网址必须带上——模型要「据 XX」说，用户要能点开核实。
    /// 任何坏输入（空、非 JSON）都返回一句兜底话，绝不抛异常：
    /// 搜索是加分项，不能因为它坏了整轮对话。
    /// </summary>
    public static string Format(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);

            var lines = new List<string>();

            if (doc.RootElement.TryGetProperty("answer", out var answer)
                && answer.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(answer.GetString()))
            {
                lines.Add("综合回答：" + answer.GetString()!.Trim());
            }

            if (doc.RootElement.TryGetProperty("results", out var results)
                && results.ValueKind == JsonValueKind.Array)
            {
                foreach (var r in results.EnumerateArray())
                {
                    var title = r.TryGetProperty("title", out var t) && t.ValueKind == JsonValueKind.String
                        ? t.GetString()
                        : null;
                    var url = r.TryGetProperty("url", out var u) && u.ValueKind == JsonValueKind.String
                        ? u.GetString()
                        : null;
                    var content = r.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.String
                        ? c.GetString()
                        : null;

                    // 一条结果至少要有点东西，标题/网址/摘要全空就跳过。
                    if (string.IsNullOrWhiteSpace(url) && string.IsNullOrWhiteSpace(content))
                    {
                        continue;
                    }

                    lines.Add($"- {title ?? "（无标题）"} | {url}");
                    if (!string.IsNullOrWhiteSpace(content))
                    {
                        lines.Add($"  {content}");
                    }
                }
            }

            return lines.Count == 0 ? "（这次搜索没返回任何结果。）" : string.Join('\n', lines);
        }
        catch (JsonException)
        {
            return "（搜索结果解析不了。）";
        }
    }

    /// <summary>
    /// 把博查（Bocha）的响应体拼成模型看得懂的文字。
    /// 博查的返回结构和 Tavily 不同：code + data.webPages.value[].{name,url,summary,snippet}，
    /// 没有 Tavily 那种顶层的 answer，而是每条结果自带一段 AI 摘要（summary）。
    /// </summary>
    public static string FormatBocha(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);

            if (doc.RootElement.TryGetProperty("code", out var code)
                && code.ValueKind == JsonValueKind.Number
                && code.GetInt32() != 200)
            {
                return "（搜索返回错误。）";
            }

            if (!doc.RootElement.TryGetProperty("data", out var data)
                || !data.TryGetProperty("webPages", out var webPages)
                || !webPages.TryGetProperty("value", out var value)
                || value.ValueKind != JsonValueKind.Array)
            {
                return "（这次搜索没返回任何结果。）";
            }

            var lines = new List<string>();

            foreach (var r in value.EnumerateArray())
            {
                var title = r.TryGetProperty("name", out var t) && t.ValueKind == JsonValueKind.String
                    ? t.GetString()
                    : null;
                var url = r.TryGetProperty("url", out var u) && u.ValueKind == JsonValueKind.String
                    ? u.GetString()
                    : null;
                var summary = r.TryGetProperty("summary", out var s) && s.ValueKind == JsonValueKind.String
                    ? s.GetString()
                    : null;
                var snippet = r.TryGetProperty("snippet", out var sn) && sn.ValueKind == JsonValueKind.String
                    ? sn.GetString()
                    : null;

                var content = !string.IsNullOrWhiteSpace(summary) ? summary : snippet;

                // 没有正文的（报纸首页、版面链接这类）对提取事实没用，跳过。
                // 只留「标题+网址」的空壳，模型读不到内容，等于没搜到——
                // 11-24 实测它拿到的就是山西日报/济宁日报的首页链接，念不出正文。
                if (string.IsNullOrWhiteSpace(content))
                {
                    continue;
                }

                lines.Add($"- {title ?? "（无标题）"} | {url}");
                lines.Add($"  {content}");
            }

            return lines.Count == 0 ? "（这次搜索没返回任何结果。）" : string.Join('\n', lines);
        }
        catch (JsonException)
        {
            return "（搜索结果解析不了。）";
        }
    }
}
