using DayManager.Core.Entities;
using DayManager.Core.Enums;
using DayManager.Core.Planning;
using Microsoft.EntityFrameworkCore;

namespace DayManager.Data.Repositories;

/// <summary>
/// 项目（两级结构里的上层）的读写。
///
/// 【为什么 AI 拆解出来的东西要落成"项目 + 任务"而不是"父任务 + 子任务"】
/// 用 items 表自身的父子关系（ParentTaskId）也能表达两层，但会踩一个坑：
/// 今日清单会把父子两条都查出来，于是同一件事在界面上出现两次。
/// 而项目是独立的一张表，今日清单只查 items，父子重复的问题从结构上就不存在了。
///
/// 更重要的是语义：用户说的"写一份结题报告"，本质上就是一个项目——
/// 它有名字、有截止日、有进度（子任务完成比例）。
/// 用项目来表达，进度条、归档、颜色这些已有能力立刻就能用上，
/// 而不是以后再补一套。
/// </summary>
/// <summary>
/// 列表里"一个项目"要显示的全部内容。
///
/// 【为什么单独定义一个类型，而不是直接返回 Project 实体】
/// 界面要的（已完成几条、还剩几条、下一件什么时候到期）没有一样是 Project 的字段，
/// 全是算出来的。如果返回实体，界面就得自己再算一遍，
/// 或者在页面里塞一堆查询代码——两种都不该出现在 View 层。
/// 这个类型就是"界面要什么，这里就给什么"的契约。
/// </summary>
/// <param name="ActiveCount">进度分母：放弃的和删掉的不算，所以进度条能走到 100%。</param>
/// <param name="AbandonedCount">放弃了几条。单独给出而不是混进分母，界面上可以说明"放弃了 2 条"。</param>
/// <param name="NextDueAt">下一件还没做的事的截止时间。这是打开大任务页真正想看的那个数字。</param>
public sealed record ProjectSummary(
    Guid Id,
    string Name,
    string? Color,
    DateTimeOffset? TargetDate,
    int DoneCount,
    int ActiveCount,
    int AbandonedCount,
    DateTimeOffset? NextDueAt);

public class ProjectRepository
{
    private readonly IDbContextFactory<AppDbContext> _dbFactory;

    public ProjectRepository(IDbContextFactory<AppDbContext> dbFactory)
    {
        _dbFactory = dbFactory;
    }

    /// <summary>
    /// 一次性建好项目以及挂在它下面的所有任务。
    ///
    /// 【为什么整个操作只调用一次 SaveChanges】
    /// EF Core 的 SaveChanges 默认包在一个事务里：
    /// 要么全部成功，要么全部回滚。如果每加一条就保存一次，
    /// 中途出错就会留下"项目建好了、任务只写进去一半"的残缺数据，
    /// 而残缺的计划比没有计划更糟——你以为排好了，其实缺了几条。
    /// 一次性提交，这个问题从根上不存在。
    /// </summary>
    /// <param name="name">项目名（就是用户原始输入的那件事）。</param>
    /// <param name="targetDate">项目目标完成日期，可以为空。</param>
    /// <param name="tasks">已经摊好日期的子任务。</param>
    public async Task<Project> CreateWithTasksAsync(
        string name,
        DateOnly? targetDate,
        IReadOnlyList<ScheduledTask> tasks,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("项目名不能为空", nameof(name));
        }

        await using var db = await _dbFactory.CreateDbContextAsync(ct);

        var project = new Project
        {
            Name = name.Trim(),

            // 目标日期存当天最后一刻，理由和任务截止时间一样：
            // 存零点的话项目在当天一开始就"已经过期"了。
            TargetDate = targetDate is null ? null : DateHelper.EndOfDay(targetDate.Value),

            Status = ProjectStatus.Active
        };

        db.Projects.Add(project);

        var order = 0;
        foreach (var scheduled in tasks)
        {
            var task = scheduled.Task;

            db.Items.Add(new Item
            {
                Title = task.Title.Trim(),
                Project = project,
                ProjectId = project.Id,

                // 【为什么截止时间取当天最后一刻，而不是当天零点】
                // 见 DateHelper.EndOfDay 的说明：取零点的话任务在当天刚一开始就"已逾期"了。
                DueAt = DateHelper.EndOfDay(scheduled.Date),

                // 摊到各天的子任务是定日、做一次的叶子。
                // 不写 Shape 会走 AddAsync 的时间兜底，虽然结果一样，
                // 但「分类靠推断」正是 11-21 事故的起点，所有正经入口都显式写。
                Shape = ItemShape.DayTask,
                CompletionMode = CompletionMode.Once,
                VerdictMode = VerdictMode.Judged,

                EstimateMinutes = task.EstimateMinutes,
                MinimalStep = task.MinimalStep,
                Kind = task.Kind,

                // 保留 AI 给出的顺序，这样界面上看到的就是"先做这个、再做那个"，
                // 而不是被创建时间或者标题打乱。
                SortOrder = order++,

                TimeZoneId = TimeZoneInfo.Local.Id
            });
        }

        await db.SaveChangesAsync(ct);
        return project;
    }

    /// <summary>
    /// 列出所有进行中的项目，附带每个项目的任务完成情况。
    /// </summary>
    public async Task<List<Project>> GetActiveAsync(CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);

        return await db.Projects
            .Where(p => p.Status == ProjectStatus.Active)
            .OrderBy(p => p.TargetDate ?? DateTimeOffset.MaxValue)
            .ThenBy(p => p.CreatedAt)
            .ToListAsync(ct);
    }

    /// <summary>
    /// 一次性取出所有进行中的项目以及各自的进度。
    ///
    /// 【为什么不能循环调用 GetProgressAsync】
    /// 十个项目就是十一次数据库往返。单机 SQLite 感觉不出来，
    /// 但换成手机端连家里的机器时就变成十一次网络往返，列表会明显卡一下。
    /// 这里用两条 GROUP BY 把所有项目的统计一次取回，再在内存里拼起来，
    /// 恒定的两次查询，跟项目多少无关。
    /// </summary>
    public async Task<List<ProjectSummary>> GetActiveWithProgressAsync(CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);

        var projects = await db.Projects
            .Where(p => p.Status == ProjectStatus.Active)
            .OrderBy(p => p.TargetDate ?? DateTimeOffset.MaxValue)
            .ThenBy(p => p.CreatedAt)
            .ToListAsync(ct);

        if (projects.Count == 0)
        {
            return new List<ProjectSummary>();
        }

        var ids = projects.Select(p => p.Id).ToList();

        // 第一条：每个项目每种状态各有几条。
        // 软删的不用管——AppDbContext 里配了全局查询过滤器，查出来的必然是没删的。
        //
        // 【只数叶子】容器（阶段性目标）挂在同一个项目下时，它自己没有完成按钮，
        // 进度由下级汇总。把它算进分母，同一件事会算两次（父一条 + 子若干条），
        // 进度条永远走不到 100%。这是 ② 可递归落地时必须同步改的一处。
        var counts = await db.Items
            .Where(x => x.ProjectId != null && ids.Contains(x.ProjectId.Value)
                        && x.CompletionMode != CompletionMode.ByChildren)
            .GroupBy(x => new { x.ProjectId, x.Status })
            .Select(g => new { g.Key.ProjectId, g.Key.Status, Count = g.Count() })
            .ToListAsync(ct);

        // 第二条：每个项目下一件该做的是什么时候到期。
        // 这是"打开大任务页最想知道的一件事"——不是"完成 60%"，
        // 而是"下一件什么时候必须交"。
        var nextDue = await db.Items
            .Where(x => x.ProjectId != null
                        && ids.Contains(x.ProjectId.Value)
                        && x.Status != ItemStatus.Done
                        && x.Status != ItemStatus.Abandoned
                        && x.CompletionMode != CompletionMode.ByChildren)
            .GroupBy(x => x.ProjectId!.Value)
            .Select(g => new { ProjectId = g.Key, Next = g.Min(x => x.DueAt) })
            .ToDictionaryAsync(x => x.ProjectId, x => x.Next, ct);

        var byProject = counts
            .GroupBy(x => x.ProjectId!.Value)
            .ToDictionary(g => g.Key, g => g.ToDictionary(x => x.Status, x => x.Count));

        return projects.Select(p =>
        {
            byProject.TryGetValue(p.Id, out var stat);
            var done = Stat(stat, ItemStatus.Done);
            var abandoned = Stat(stat, ItemStatus.Abandoned);

            //
            // 【项目进度的分母为什么不把"放弃"算进去】
            // 完成率的分母要包含放弃（那是承认失败，必须计入），
            // 但这里的进度条回答的是另一个问题："这件事还剩多少要做"。
            // 一条已经明确不做了的子任务，不属于"还要做"的部分。
            // 算进去的后果很具体：一个做了八条、放弃两条的项目，
            // 进度条永远卡在 80%，看着像永远做不完——
            // 而"永远做不完的进度条"正是这个软件要消灭的那种挫败感。
            //
            var active = stat is null
                ? 0
                : stat.Where(x => x.Key != ItemStatus.Abandoned).Sum(x => x.Value);

            return new ProjectSummary(
                p.Id, p.Name, p.Color, p.TargetDate,
                done, active, abandoned,
                nextDue.TryGetValue(p.Id, out var next) ? next : null);
        }).ToList();
    }

    private static int Stat(Dictionary<ItemStatus, int>? stat, ItemStatus status) =>
        stat is not null && stat.TryGetValue(status, out var n) ? n : 0;

    /// <summary>
    /// 所有项目（含归档）以及挂在各自下面的任务，一次取回。
    ///
    /// 【为什么要有这个，而不是让调用方循环 GetItemsAsync】
    /// 循环调用就是「项目数 + 1」次数据库往返；项目会越攒越多，
    /// 而对话每轮都要拼一次视野。这里两条查询把项目和带归属的任务一次取回、
    /// 在内存里分组，往返次数跟项目多少无关（和 GetActiveWithProgressAsync 同一个理由）。
    ///
    /// 【为什么连归档的也给】
    /// 「软件里所有信息 AI 都能看到」是定过的口径。他问「之前那个竞赛归到哪了」时，
    /// 归档项目同样是答案；只给进行中的，模型又会答一次「我看不到」。
    /// 这里只是把已有的信息读出来喂给 AI，不加字段、不加动作、不改数据模型。
    /// </summary>
    public async Task<List<(Project Project, List<Item> Items)>> GetAllWithItemsAsync(
        CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);

        // 进行中的排前面，归档的沉底。不依赖枚举的具体数值。
        var projects = await db.Projects
            .OrderBy(p => p.Status == ProjectStatus.Archived ? 1 : 0)
            .ThenBy(p => p.CreatedAt)
            .ToListAsync(ct);

        var ids = projects.Select(p => p.Id).ToList();
        var items = await db.Items
            .Where(x => x.ProjectId != null && ids.Contains(x.ProjectId.Value))
            .ToListAsync(ct);

        return projects.Select(p => (
            p,
            items.Where(i => i.ProjectId == p.Id)
                 .OrderBy(i => i.SortOrder)
                 .ThenBy(i => i.CreatedAt)
                 .ToList()))
            .ToList();
    }

    /// <summary>
    /// 一个项目下面的所有任务，没做完的排在前面。
    ///
    /// 【为什么要把做完的沉到底，而不是按时间排】
    /// 打开一个大任务时，要看的是"接下来做什么"，不是"我做过什么"。
    /// 完成的部分往后挪，视线第一落点就是还没动的那些。
    /// </summary>
    public async Task<List<Item>> GetItemsAsync(Guid projectId, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);

        var items = await db.Items
            .Where(x => x.ProjectId == projectId)
            .ToListAsync(ct);

        return items
            .OrderBy(x => x.Status == ItemStatus.Done || x.Status == ItemStatus.Abandoned ? 1 : 0)
            .ThenBy(x => x.DueAt ?? DateTimeOffset.MaxValue)
            .ThenBy(x => x.SortOrder)
            .ToList();
    }

    /// <summary>改项目名。</summary>
    public async Task RenameAsync(Guid projectId, string name, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        await using var db = await _dbFactory.CreateDbContextAsync(ct);

        var project = await db.Projects.FindAsync([projectId], ct);
        if (project is null)
        {
            return;
        }

        project.Name = name.Trim();

        await db.SaveChangesAsync(ct);
    }

    /// <summary>改目标完成日期。传 null 表示"还没定什么时候做完"。</summary>
    public async Task SetTargetDateAsync(Guid projectId, DateOnly? date, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);

        var project = await db.Projects.FindAsync([projectId], ct);
        if (project is null)
        {
            return;
        }

        project.TargetDate = date is null ? null : DateHelper.EndOfDay(date.Value);

        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// 归档。
    ///
    /// 【为什么不提供真删除】
    /// 用户要的是"这件事结束了，别再出现在列表里"，
    /// 而删除还会把底下所有任务一并抹掉，历史完成率数据就断了。
    /// 归档在界面上的效果和删除一样，代价是零。
    /// </summary>
    public async Task ArchiveAsync(Guid projectId, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);

        var project = await db.Projects.FindAsync([projectId], ct);
        if (project is null)
        {
            return;
        }

        project.Status = ProjectStatus.Archived;

        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// 建一个空项目（手动新建，不经过 AI）。
    ///
    /// 【为什么要有手动入口】
    /// AI 不可用的时候（离线只读、key 过期、后端没起来），
    /// 至少还能自己建一个容器把事情归拢起来。
    /// 一个"只有 AI 能用"的功能，在 AI 挂掉的那一刻就变成零。
    /// </summary>
    public async Task<Project> CreateAsync(string name, DateOnly? targetDate, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("项目名不能为空", nameof(name));
        }

        await using var db = await _dbFactory.CreateDbContextAsync(ct);

        var project = new Project
        {
            Name = name.Trim(),
            TargetDate = targetDate is null ? null : DateHelper.EndOfDay(targetDate.Value),
            Status = ProjectStatus.Active
        };

        db.Projects.Add(project);
        await db.SaveChangesAsync(ct);

        return project;
    }

    /// <summary>
    /// 算一个项目的完成进度。
    ///
    /// 【为什么不存一个"进度百分比"字段】
    /// 存下来就要在每次改动任务时同步更新它，漏一处就和其他数据对不上，
    /// 而这种不一致是静默的——界面显示 60%，实际是 70%，没人会发现。
    /// 每次用的时候现算，永远和真实数据一致，省掉一整类 bug。
    /// 数据量是一个人用的规模，现算的开销可以忽略。
    /// </summary>
    public async Task<(int Done, int Total)> GetProgressAsync(Guid projectId, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);

        var counts = await db.Items
            .Where(x => x.ProjectId == projectId)
            .GroupBy(x => x.Status)
            .Select(g => new { Status = g.Key, Count = g.Count() })
            .ToListAsync(ct);

        var total = counts.Sum(x => x.Count);
        var done = counts.Where(x => x.Status == ItemStatus.Done).Sum(x => x.Count);

        return (done, total);
    }
}
