namespace DayManager.Core.Planning;

/// <summary>
/// 把 AI 回复里"模型对自己说的话"剔掉。
///
/// 【为什么会有这个类】
/// 实测出现过这样的回复（已存进过用户的库）：
///   "他只是在打招呼，我上一条把他当成要启动任务了……可以说：没别的意思，就是打招呼。
///    保持两三句。actions 为空。"
/// 前半段是它在自言自语，后半段是它在给自己下输出格式的指令。
/// 用户看到的是"AI 在念叨一堆我看不懂的话"。
///
/// 【这只是第二道防线，第一道在提示词里】
/// 主修法是给模型的 JSON 加一个 think 字段，让它的思考有地方放——
/// 它不用往 reply 里塞，就不会塞。这里处理的是"提示词没拦住"的漏网情况。
///
/// 【为什么放在 Core 而不是 Api】
/// 它需要被冒烟测试直接调用。这种"改错了标点用户立刻看得见"的逻辑，
/// 不能只在真实对话里碰运气验证——第一版就是这么出的问题
/// （切句时丢了原标点，"你能做吗？"变成"你能做吗？。"）。
/// </summary>
public static class ReplyCleaner
{
    /// <summary>
    /// 系统内部的接口字段名。
    ///
    /// 【判据为什么选这几个词】
    /// 用户根本不知道 actions / suggestions / reply 这些概念的存在，
    /// 它们是程序和模型之间的约定。所以只要这些词出现在给用户看的回复里，
    /// 100% 是模型泄漏，不需要靠"这句话像不像自言自语"去猜。
    /// 猜就会误伤——正常回复里确实会出现"他在想什么"这种句子，
    /// 而"actions 为空"永远不会正常出现。
    ///
    /// 【为什么 questions 还留在名单里，它已经不是字段名了】
    /// 那个字段改成了 suggestions（追问改成在 reply 里直接用嘴问）。
    /// 但模型见过旧格式——历史对话里有，下一版模型也可能还记得。
    /// 这个词留着拦的是"它照旧格式自言自语"，多留一个词的代价是零。
    /// </summary>
    private static readonly string[] InternalMarkers =
    {
        "actions", "suggestions", "questions", "think", "reply", "json"
    };

    /// <summary>
    /// 剔除混进来的元数据句子。没有要剔的就原样返回（包括原标点）。
    /// </summary>
    public static string Clean(string? reply)
    {
        if (string.IsNullOrWhiteSpace(reply))
        {
            return string.Empty;
        }

        // 按句子切开，但**保留原来的标点**。
        // 第一版是按 '。' 切完再统一补一个句号，结果"你现在能做吗？"变成了
        // "你现在能做吗？。"——标点被换掉了，看着比自言自语还别扭。
        // 用零宽后顾断言按位置切，终止符留在它本来所属的那一段里。
        var kept = reply
            .Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries)
            .SelectMany(line => System.Text.RegularExpressions.Regex.Split(line, "(?<=[。！？；])"))
            .Where(s => !InternalMarkers.Any(m => s.Contains(m, StringComparison.OrdinalIgnoreCase)))
            .Select(s => s.Trim())
            .Where(s => s.Length > 0)
            .ToList();

        return kept.Count == 0 ? string.Empty : string.Concat(kept);
    }
}
