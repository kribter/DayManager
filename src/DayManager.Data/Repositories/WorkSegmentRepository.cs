using DayManager.Core.Entities;
using Microsoft.EntityFrameworkCore;

namespace DayManager.Data.Repositories;

/// <summary>
/// 任务计时段的读取。写不在这里——见下面那条说明。
///
/// 【为什么写操作放在 ItemRepository，不在这里】
/// 开关一段计时和"改任务状态"必须是同一件事：
/// 按启动就要同时开段，暂停/完成就要同时闭段。
/// 分到两个仓储里，就得开两个 DbContext 写同一个 SQLite 文件，
/// 而 SQLite 在同一时刻只允许一个写者，这种写法迟早撞出 "database is locked"。
/// 所以写跟着状态走（在 ItemRepository 里，同一个上下文内完成），
/// 这里只负责读——读没有并发问题。
/// </summary>
public class WorkSegmentRepository
{
    private readonly IDbContextFactory<AppDbContext> _factory;

    public WorkSegmentRepository(IDbContextFactory<AppDbContext> factory)
    {
        _factory = factory;
    }

    /// <summary>
    /// 取与 [from, to) 有交集的所有计时段。
    ///
    /// 【为什么"还在走"的那一段也要算进来】
    /// 他此刻可能正在纸上做题，还没点完成。
    /// 那段正是这次要补进分母的部分——等到点完成才计入的话，
    /// 他看着时间页上的占比仍然是被冤枉的那个数。
    /// 库里它的 EndAt 是空，取回来之后用"现在"补上。
    /// </summary>
    public async Task<List<WorkSegment>> GetOverlappingAsync(
        DateTimeOffset from,
        DateTimeOffset to,
        CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);

        // 还没结束的段（EndAt == null）右端是无限的，不能被时间条件挡在外面。
        // 注意别写成 x.EndAt >= from——null 参与比较的结果是"不成立"，
        // 那一段会静默消失，而且从代码上看不出任何异常。
        var rows = await db.WorkSegments
            .Where(x => x.StartAt < to && (x.EndAt == null || x.EndAt >= from))
            .ToListAsync(ct);

        var now = DateTimeOffset.Now;
        foreach (var row in rows)
        {
            row.EndAt ??= now;
        }

        return rows;
    }

    /// <summary>某一条任务分了几段做完。冒烟测试用它验证「启动→暂停」是否真接上了 work_segments 表。</summary>
    public async Task<int> CountSegmentsAsync(Guid itemId, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);

        return await db.WorkSegments.CountAsync(x => x.ItemId == itemId, ct);
    }
}
