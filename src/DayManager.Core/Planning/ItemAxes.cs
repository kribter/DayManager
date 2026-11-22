using DayManager.Core.Entities;
using DayManager.Core.Enums;

namespace DayManager.Core.Planning;

/// <summary>
/// 记录上的判据，收在唯一一处。
///
/// 【为什么是纯函数、放在 Core】
/// 界面、完成率、AI 提示词、离线兜底、冒烟要用同一份答案。
/// 以前这些出口各猜一遍：界面看 StartAt 可空、AI 靠模型自己读标题、
/// 离线兜底把「压根没排时间」一律当成「随时能做」——
/// 22:44 那句「先做『0点上床睡觉』，就 10 分钟」就是各猜一遍的结果。
/// 分类落地之后，这里变成唯一真相源，谁都不许自己再推一遍。
///
/// 【分类不是一个能自由组合的坐标系】
/// 库里就是一个显式枚举（Shape）+ 几个可空属性 / 开关 + 一根父子关系。
/// 「是不是容器」读 CompletionMode，「判不判成败」由截止日和时间轴读出来，
/// 都不是额外一根轴。Deferred 随 DueAt 滑动、会自己翻成 Judged 或欠账；
/// Never 由 VerdictMode 声明、永不翻。这里这个枚举是「读出来的三带」，
/// 不是存进数据库的字段。
/// </summary>
public static class ItemAxes
{
    /// <summary>此刻这条判不判成败、要不要为它产生压力。</summary>
    public enum Verdict
    {
        /// <summary>判：进清单、进分母，没做滚欠账。</summary>
        Judged,

        /// <summary>暂时不判：定段常驻可见，到截止日翻成 Judged。</summary>
        Deferred,

        /// <summary>一直不判：守住类，永不进分母、不许被点名。</summary>
        Never
    }

    /// <summary>
    /// 一条记录此刻落在哪一格。参数全是值，不碰数据库也不碰时钟（today 由调用方给）。
    /// </summary>
    public static Verdict VerdictOf(
        ItemShape shape,
        VerdictMode mode,
        DateTimeOffset? dueAt,
        DateOnly today)
    {
        // 一直不判是声明，优先级最高——哪怕它长得像一条待办，也不该被催。
        if (mode == VerdictMode.Never)
        {
            return Verdict.Never;
        }

        // 暂时不判是算出来的：只有定段、且截止日还在未来时成立。
        // 到截止日当天，同一个条件自然变假，翻成正经待办；过期则交给既有逾期口径。
        // 这里自己按本地切天，不调 ItemPresence.LocalDay——
        // 让依赖保持单向（ItemPresence 会用本类算常驻），两个纯函数不互相引用。
        if (shape == ItemShape.DeadlineOnly
            && dueAt is { } due
            && DateOnly.FromDateTime(due.ToLocalTime().DateTime) > today)
        {
            return Verdict.Deferred;
        }

        return Verdict.Judged;
    }

    /// <summary>便捷重载：直接吃实体。冒烟和界面都走这个。</summary>
    public static Verdict VerdictOf(Item item, DateOnly today) =>
        VerdictOf(item.Shape, item.VerdictMode, item.DueAt, today);

    /// <summary>
    /// 这条该不该出现在「今天该做完」的清单里，进不进完成率分母。
    ///
    /// 【为什么容器和守住类都不进】
    /// 容器没有完成按钮，进度由叶子汇总；把它算进分母，同一件事会算两次。
    /// 守住类没有完成动作，算进去只会让分母天天难看——
    /// 正好撞上这个软件要对抗的「完不成 → 挫败 → 开摆」。
    /// 定段常驻项由 GetFloatingDeadlinesAsync 单独成带，天然不在这里。
    /// </summary>
    public static bool EntersDayList(Item item) =>
        !IsContainer(item)
        && item.VerdictMode != VerdictMode.Never;

    /// <summary>
    /// 这条是不是容器（阶段性目标 / 大任务壳）。
    ///
    /// 【为什么只有这一个答案】
    /// 11-22 之前这里有两套判据：一个读「有没有子项」，一个读 CompletionMode。
    /// 同一个问题两个答案，早晚会在某一处不一致（提案 2.3 点名的那处）。
    /// 现在统一到声明本身：容器靠 CompletionMode.ByChildren 认，
    /// 层级靠 ParentTaskId 自引用表达——两件事，别混。
    /// 清单准入、完成率分母、离线点名、大任务进度、树组装全走这里。
    /// </summary>
    public static bool IsContainer(Item item) =>
        item.CompletionMode == CompletionMode.ByChildren;

    /// <summary>叶子 = 不是容器。进度和完成率只数它。</summary>
    public static bool IsLeaf(Item item) => !IsContainer(item);

    /// <summary>
    /// 这条能不能被 AI / 离线兜底点名去「先做 10 分钟」。
    ///
    /// 【为什么不许点名守住类】
    /// 「0点上床睡觉」没有「做 10 分钟」这个动作；套上最小启动步骤就成了「先睡 10 分钟」。
    /// 离线兜底不需要语义理解，只要读到 VerdictMode 就知道不该点名。
    /// 定段常驻项也不点名：它明确不算今天该做完。
    /// </summary>
    public static bool CanNudge(Item item, DateOnly today) =>
        VerdictOf(item, today) == Verdict.Judged
        && IsLeaf(item)
        && item.Status is not (ItemStatus.Done or ItemStatus.Abandoned);
}
