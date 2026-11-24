using System.Text.RegularExpressions;

namespace DayManager.Core.Ai;

/// <summary>
/// 从坏掉的 JSON 里尽量捞出「reply」那句话。
///
/// 【为什么要有这个】
/// 模型偶尔会输出坏 JSON（键丢了、引号串了），这时整段直接反序列化会失败。
/// 原来的兜底是把整段原样当回复甩给用户——于是用户会看到 think 里的自言自语
/// （"别装专家、别编内幕……"）和一堆大括号、引号。这里尽量把 reply 捞出来，
/// 捞不到就返回 null，由上层给一句能接住的话，绝不让内部独白漏给用户。
///
/// 【为什么放 Core】
/// 纯函数（进字符串、出字符串），符合 Core「零依赖纯逻辑」的规矩，
/// 冒烟能直接喂一段坏 JSON 验证，不用真触发一次模型。
/// </summary>
public static class ReplySalvage
{
    public static string? Salvage(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return null;
        }

        // ① 带键的："reply": "..." 或 "reply":"..."
        var keyed = Regex.Match(content, "\"reply\"\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)*)\"");
        if (keyed.Success)
        {
            return Regex.Unescape(keyed.Groups[1].Value);
        }

        // ② 键全丢了、模型把 think/reply 裸写，形如 {…think…",…reply…",}：
        //    最后两个 '",' 之间夹的多半是 reply。
        var quoteCommas = Regex.Matches(content, "\",");
        if (quoteCommas.Count >= 2)
        {
            var from = quoteCommas[quoteCommas.Count - 2];
            var to = quoteCommas[quoteCommas.Count - 1];
            var candidate = content[(from.Index + from.Length)..to.Index].Trim();
            if (candidate.Length > 0)
            {
                return candidate;
            }
        }

        return null;
    }
}
