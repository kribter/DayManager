using DayManager.Core.Enums;

namespace DayManager.Data.Snapshots;

/// <summary>
/// 一条记录在某一个时刻的样子（用于撤销与冲突检测）。
///
/// 【为什么不直接把 Item 整个序列化存进日志】
/// 因为 Item 里带着 Version、UpdatedAt、DeviceId 这些**同步元数据**，
/// 它们每次保存都会变。拿整条记录做比较的话，哪怕业务字段一个没动，
/// 前后快照也永远不相等——冲突检测就会变成"永远报冲突"，等于没有。
/// 所以这里只挑业务字段：改过才算改过。
///
/// 【为什么字段要显式列出来，不用反射自动拷贝】
/// 反射看起来省事，但它会把将来新增的字段**静默**纳入快照。
/// 比如哪天加了一个"忽略同步"的内部字段，撤销就会去改它，
/// 而这类问题只在特定操作序列下才暴露，极难复现。
/// 显式列出意味着：新增字段时必须有人想一想"它要不要被撤销"。
/// </summary>
public sealed record ItemSnapshot
{
    public string Title { get; init; } = string.Empty;
    public string? Notes { get; init; }
    public ItemKind Kind { get; init; }

    // 三根轴 + 层级。都是业务字段：AI 改了形态或把任务挂到别的阶段下，
    // 撤销必须还原，否则「撤了但分类没回去」是静默的数据错。
    public ItemShape Shape { get; init; }
    public CompletionMode CompletionMode { get; init; }
    public VerdictMode VerdictMode { get; init; }
    public Guid? ParentTaskId { get; init; }

    public DateTimeOffset? StartAt { get; init; }
    public DateTimeOffset? EndAt { get; init; }
    public DateTimeOffset? DueAt { get; init; }
    public bool IsAllDay { get; init; }

    public ItemStatus Status { get; init; }
    public DateTimeOffset? StartedAt { get; init; }
    public DateTimeOffset? CompletedAt { get; init; }
    public DateTimeOffset? SkippedAt { get; init; }
    public string? SkipReason { get; init; }
    public int SkipCount { get; init; }

    public int? EstimateMinutes { get; init; }
    public int? ActualMinutes { get; init; }
    public string? MinimalStep { get; init; }
    public Priority Priority { get; init; }
    public Guid? ProjectId { get; init; }
    public int SortOrder { get; init; }

    /// <summary>被软删除了吗。撤销"删除"与撤销"修改"是两回事，必须分开记。</summary>
    public bool IsDeleted { get; init; }

    /// <summary>
    /// 把一个时刻归一化为"数据库里存出来的样子"：UTC + 毫秒精度。
    ///
    /// 【这里踩过两个真实的坑，都是同一个症状：撤销永远报冲突】
    ///
    /// 坑一，时区偏移不一致。内存里的对象是本地时间（+08:00），
    /// 从数据库读回来的是 UTC（+00:00），因为存储层统一按 Unix 毫秒存。
    /// 同一个瞬间，序列化成字符串却长得不一样（"23:30+08:00" vs "15:30+00:00"）。
    ///
    /// 坑二，精度不一致。DateTimeOffset.Now 精确到 100 纳秒，
    /// 存进 SQLite 后只剩毫秒。于是"刚写完的内存对象"和"读回来的同一条记录"
    /// 序列化后差了四位小数。
    ///
    /// 两个坑合起来的后果是：快照比较**永远**不相等，
    /// 撤销每一次都误报"这之后被改过"，等于整个撤销功能失效。
    /// 而这种 bug 不报错、不抛异常，只表现为"点了撤销没反应"。
    ///
    /// 解法是照搬存储层的做法（Unix 毫秒）：同一个时刻永远得到同一个字符串。
    /// </summary>
    private static DateTimeOffset? Norm(DateTimeOffset? value)
    {
        if (value is null)
        {
            return null;
        }

        return DateTimeOffset.FromUnixTimeMilliseconds(value.Value.ToUniversalTime().ToUnixTimeMilliseconds());
    }

    /// <summary>从实体抽出快照（时间已归一化，见 Norm 的说明）。</summary>
    public static ItemSnapshot From(DayManager.Core.Entities.Item item) => new()
    {
        Title = item.Title,
        Notes = item.Notes,
        Kind = item.Kind,
        Shape = item.Shape,
        CompletionMode = item.CompletionMode,
        VerdictMode = item.VerdictMode,
        ParentTaskId = item.ParentTaskId,
        StartAt = Norm(item.StartAt),
        EndAt = Norm(item.EndAt),
        DueAt = Norm(item.DueAt),
        IsAllDay = item.IsAllDay,
        Status = item.Status,
        StartedAt = Norm(item.StartedAt),
        CompletedAt = Norm(item.CompletedAt),
        SkippedAt = Norm(item.SkippedAt),
        SkipReason = item.SkipReason,
        SkipCount = item.SkipCount,
        EstimateMinutes = item.EstimateMinutes,
        ActualMinutes = item.ActualMinutes,
        MinimalStep = item.MinimalStep,
        Priority = item.Priority,
        ProjectId = item.ProjectId,
        SortOrder = item.SortOrder,
        IsDeleted = item.DeletedAt != null
    };

    /// <summary>
    /// 把快照写回实体。
    ///
    /// 【为什么 StartedAt 不在这里被清掉】
    /// 见 ItemRepository 的说明：首次开始时间是不可修改的事实。
    /// 撤销"完成"回到"未开始"时，你曾经动过手这件事依然成立，
    /// 抹掉它等于把已经发生的进步删掉。
    /// </summary>
    public void ApplyTo(DayManager.Core.Entities.Item item)
    {
        item.Title = Title;
        item.Notes = Notes;
        item.Kind = Kind;
        item.Shape = Shape;
        item.CompletionMode = CompletionMode;
        item.VerdictMode = VerdictMode;
        item.ParentTaskId = ParentTaskId;
        item.StartAt = StartAt;
        item.EndAt = EndAt;
        item.DueAt = DueAt;
        item.IsAllDay = IsAllDay;
        item.Status = Status;
        item.CompletedAt = CompletedAt;
        item.SkippedAt = SkippedAt;
        item.SkipReason = SkipReason;
        item.SkipCount = SkipCount;
        item.EstimateMinutes = EstimateMinutes;
        item.ActualMinutes = ActualMinutes;
        item.MinimalStep = MinimalStep;
        item.Priority = Priority;
        item.ProjectId = ProjectId;
        item.SortOrder = SortOrder;

        // StartedAt 只补不删：撤销不该抹掉"已经开始过"这个事实。
        if (StartedAt != null)
        {
            item.StartedAt ??= StartedAt;
        }

        item.DeletedAt = IsDeleted ? (item.DeletedAt ?? DateTimeOffset.Now) : null;
    }
}
