namespace DayManager.Core.Planning;

/// <summary>
/// 收拾 AI 给的"候选回复"（它会显示在输入框上方，点一下就发出去）。
///
/// 【为什么放在 Core】
/// 和 <see cref="ReplyCleaner"/> 一个理由：这段逻辑改一点点，
/// 用户第一眼就看得见（chip 上写的什么字、几个字），
/// 不能只在真实对话里碰运气——模型今天给三条明天给六条，靠肉眼轮不到验证。
///
/// 【为什么不是让模型自己守规矩】
/// 提示词里写了"最长 15 个字、给 2~3 条"，实测它执行得时好时坏：
/// 有时候乖乖给两条短的，有时候给五条，还有一次把整句复述塞了进来。
/// 模型对"长度"这类约束本来就不可靠，而这里超标的代价是界面失控
/// （一排 chip 把输入框顶出屏幕）。宁肯在这头按上限截断。
/// </summary>
public static class SuggestionCleaner
{
    /// <summary>界面一行最多摆几条。见 ChatViewModel 里同名的说明。</summary>
    public const int MaxCount = 4;

    /// <summary>一条候选最多几个字。再长 chip 就要换行，会把输入框挤下去。</summary>
    public const int MaxLength = 24;

    /// <summary>
    /// 收拾一遍：去空白、合并换行、限长、去重、限条数。没有可用的就返回 null。
    /// </summary>
    public static List<string>? Clean(IEnumerable<string>? raw)
    {
        if (raw is null)
        {
            return null;
        }

        var result = new List<string>();

        foreach (var item in raw)
        {
            if (string.IsNullOrWhiteSpace(item))
            {
                continue;
            }

            // 换行在 chip 上会把按钮撑成两行，按空格接起来。
            var text = string.Join(' ', item.Split(
                new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)).Trim();

            if (text.Length == 0)
            {
                continue;
            }

            if (text.Length > MaxLength)
            {
                text = text[..MaxLength] + "…";
            }

            // 模型经常把同一个意思换两种说法输出两遍，摆在界面上就是两颗一样的按钮。
            if (!result.Contains(text, StringComparer.Ordinal))
            {
                result.Add(text);
            }

            if (result.Count == MaxCount)
            {
                break;
            }
        }

        return result.Count == 0 ? null : result;
    }
}
