namespace DayManager.Core.Entities;

/// <summary>
/// 一条 AI 关于用户的记忆（语义条目）。
/// 画像存数字、记忆存话——这是"管家记得你"的落点，和画像一起构成 AI 认识一个人的全部依据。
/// </summary>
public class AiMemory : SyncEntityBase
{
    /// <summary>记忆本身。"他的目标是 X 考试"。</summary>
    public string Content { get; set; } = string.Empty;

    /// <summary>为什么记这条。"他亲口说的，排计划用得上"。可看界面里展示，也让用户判断记得对不对。</summary>
    public string Reason { get; set; } = string.Empty;
}
