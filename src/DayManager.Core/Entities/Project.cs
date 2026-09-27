using DayManager.Core.Enums;

namespace DayManager.Core.Entities;

/// <summary>
/// 项目，两级结构（项目 → 任务）中的上层。
///
/// 【为什么要有项目这一层】
/// 你选择了两级结构而不是简单的单层标签。区别在于：
/// 单层标签只能回答"这一条属于哪个类别"；
/// 有了项目，系统才能算出"毕设完成到 60% 了"这种进度感，
/// 而进度感恰恰是对抗"高估自己 → 完不成 → 挫败"这条链路的关键正反馈素材。
/// </summary>
public class Project : SyncEntityBase
{
    public string Name { get; set; } = string.Empty;

    public string? Description { get; set; }

    /// <summary>
    /// 项目颜色，用于日历和列表里区分视觉（存十六进制字符串，如 "#4C8DFF"）。
    /// </summary>
    public string? Color { get; set; }

    /// <summary>
    /// 项目目标完成日期。可以为空——很多项目一开始并不知道什么时候能做完。
    /// </summary>
    public DateTimeOffset? TargetDate { get; set; }

    public ProjectStatus Status { get; set; } = ProjectStatus.Active;

    /// <summary>
    /// 挂在下面的所有任务 / 日程。
    ///
    /// 【为什么这里是 ICollection 且 set 是 private】
    /// 这是 EF Core 的导航属性，靠它才能写出 "查出这个项目连同它所有任务" 这种关联查询。
    /// set 设为 private 是为了禁止外部直接替换整个集合；只能通过 Add / Remove 增删元素，
    /// 这样 EF Core 才能正常追踪变动。赋初始值是为了避免集合为 null 导致空引用异常。
    /// </summary>
    public ICollection<Item> Items { get; private set; } = new List<Item>();
}
