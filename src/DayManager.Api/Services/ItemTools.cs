using System.Globalization;
using System.Text.Json;
using DayManager.Api.Models;
using DayManager.Core.Entities;
using DayManager.Core.Enums;
using DayManager.Data;
using DayManager.Data.Repositories;
using DayManager.Data.Snapshots;
using Microsoft.EntityFrameworkCore;

namespace DayManager.Api.Services;

/// <summary>
/// 执行 AI 要求的动作。
///
/// 【这一层是"AI 能管所有内容"真正落地的地方】
/// 模型只能输出文字，它说"把这三件事推到明天"并不会真的改变任何数据。
/// 中间必须有人把它翻译成数据库操作——也就是这里。
///
/// 【三条不能让步的原则】
///   1. **每一步都要留下可撤销的痕迹**：执行前存 before、执行后存 after。
///      没有这个，决策 D4（永久可撤）就是一句空话。
///   2. **不猜**。模型给了不认识的 op、不存在的 id、解析不了的日期，
///      一律明确报错并把错误说回给它，绝不"看着差不多就执行"。
///      猜错一次对用户的伤害，远大于多问一轮。
///   3. **整批共用一个批次号**。AI 一次动作改了五条，撤销必须能一次撤回来，
///      而不是让用户一条条点。
/// </summary>
public sealed class ItemTools
{
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly ItemRepository _items;
    private readonly ActionLogRepository _logs;
    private readonly ProjectRepository _projects;

    public ItemTools(
        IDbContextFactory<AppDbContext> dbFactory,
        ItemRepository items,
        ActionLogRepository logs,
        ProjectRepository projects)
    {
        _dbFactory = dbFactory;
        _items = items;
        _logs = logs;
        _projects = projects;
    }

    /// <summary>
    /// 执行一批动作。返回执行结果和批次号（没有实际改动时为 null）。
    /// </summary>
    public async Task<(List<ActionOutcome> Outcomes, Guid? BatchId)> ExecuteAsync(
        IReadOnlyList<AiAction> actions,
        string summary,
        CancellationToken ct = default)
    {
        var outcomes = new List<ActionOutcome>();
        var entries = new List<ActionLogRepository.ActionEntry>();

        foreach (var action in actions)
        {
            // 执行前：把目标现在的样子拍下来。
            // 新建的动作没有"之前"，beforeJson 留 null，撤销时意味着删掉它。
            var before = action.Id.HasValue ? await FindAsync(action.Id.Value, ct) : null;
            var beforeJson = before is null ? null : JsonSerializer.Serialize(ItemSnapshot.From(before));

            var (outcome, after) = await ApplyOneAsync(action, ct);

            if (after is not null && outcome.Ok)
            {
                var afterJson = JsonSerializer.Serialize(ItemSnapshot.From(after));
                entries.Add(new ActionLogRepository.ActionEntry(
                    ToActionType(action.Op), after.Id, beforeJson, afterJson));
            }

            outcomes.Add(outcome);
        }

        Guid? batchId = null;
        if (entries.Count > 0)
        {
            batchId = await _logs.LogBatchAsync(ActionActor.Ai, entries, summary, ct);
        }

        return (outcomes, batchId);
    }

    private async Task<(ActionOutcome Outcome, Item? After)> ApplyOneAsync(AiAction action, CancellationToken ct)
    {
        switch (action.Op?.Trim().ToLowerInvariant())
        {
            case "create":
                return await CreateAsync(action, ct);

            case "set_status":
                return await SetStatusAsync(action, ct);

            case "reschedule":
                return await RescheduleAsync(action, ct);

            case "rename":
                return await RenameAsync(action, ct);

            case "delete":
                return await DeleteAsync(action, ct);

            default:
                // 【为什么不猜一个接近的操作】
                // 模型输出了没定义的 op，最可能的原因是它没看懂工具说明，
                // 这时候替它猜一个，等于用一个错误去掩盖另一个错误。
                // 把可用操作列出来告诉它，下一轮它自己就对了。
                return (Fail(action.Op ?? "(空)",
                    $"不认识这个操作。可用的只有：create / set_status / reschedule / rename / delete。"), null);
        }
    }

    private async Task<(ActionOutcome, Item?)> CreateAsync(AiAction action, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(action.Title))
        {
            return (Fail("create", "没给标题，建不出来。"), null);
        }

        var day = ParseDate(action.Date) ?? DateOnly.FromDateTime(DateTime.Now);
        var time = ParseTime(action.Time);

        // 挂项目：同一件事拆出来的几条写同一个名字，就会归到同一个项目下。
        Guid? projectId = null;
        if (!string.IsNullOrWhiteSpace(action.Project))
        {
            projectId = await ResolveProjectAsync(
                action.Project.Trim(), ParseDate(action.ProjectTarget), ct);
        }

        var item = await _items.AddToDayAsync(action.Title.Trim(), day, time, ct);

        // AddToDayAsync 不支持带项目（它是给用户手动添加用的），
        // 所以项目归属和估时都在这里补一次写。
        if (action.EstimateMinutes is > 0 || projectId is not null)
        {
            await using var db = await _dbFactory.CreateDbContextAsync(ct);
            var entity = await db.Items.FirstAsync(x => x.Id == item.Id, ct);

            if (action.EstimateMinutes is > 0)
            {
                entity.EstimateMinutes = action.EstimateMinutes;
                item.EstimateMinutes = action.EstimateMinutes;
            }

            if (projectId is not null)
            {
                entity.ProjectId = projectId;
            }

            await db.SaveChangesAsync(ct);

            //
            // 【为什么必须重新读一次再返回】
            // 上面改的是这个 context 里的 entity，而手里这个 item 是 AddToDayAsync
            // 返回的那一个——它还是改之前的样子（没有 ProjectId、没有估时）。
            // 外面会拿返回的 item 去拍 "after" 快照，快照因此和数据库对不上，
            // 撤销时会被判成"这条后来又被改过"。
            // 实测后果：刚建的 8 条一条都撤不掉，而且报错理由完全指不到这里。
            // 重新读一次，快照才等于最终落库的样子。
            //
            item = await db.Items.AsNoTracking().FirstAsync(x => x.Id == item.Id, ct);
        }

        return (Ok("create", item.Id, item.Title,
            $"加到 {day:MM-dd}" + (time is null ? "" : time.Value.ToString(@"hh\:mm"))
            + (action.Project is null ? "" : $"，归到「{action.Project.Trim()}」")), item);
    }

    /// <summary>
    /// 按名字找到或创建一个项目。
    ///
    /// 【为什么要复用同名项目，而不是每次新建一个】
    /// 用户可能分两次说"开题报告"——第一次拆了三天，过两天又说"再加两章"。
    /// 每次都新建的话，大任务页上会出现两个同名项目，
    /// 每个都显示"完成 3/5"，没有一个是对的。
    /// 复用之后第二次拆出来的任务自动并进原来那个，进度才是真的。
    ///
    /// 【为什么不缓存名字到 id 的映射】
    /// 本类是单例，缓存会跨请求存活。万一项目在两次请求之间被归档了，
    /// 缓存会把新任务挂到一个已归档的项目上——
    /// 于是任务建好了，大任务页上却看不见它，而且没有任何报错。
    /// 每次查库，一个人用的数据量，这点开销换掉一整类静默错误，值。
    /// </summary>
    private async Task<Guid> ResolveProjectAsync(string name, DateOnly? target, CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);

        var existing = await db.Projects
            .Where(p => p.Status == ProjectStatus.Active && p.Name == name)
            .OrderBy(p => p.CreatedAt)
            .FirstOrDefaultAsync(ct);

        if (existing is not null)
        {
            // 后面再给的截止日也要生效：分两次拆的时候，
            // 第二次往往才知道整件事什么时候必须做完。
            // 走仓储方法而不是直接改字段，是因为"当天最后一刻"这条规则
            // 只有仓储里那份实现知道（DateHelper 是 Data 层内部的，这里够不着）。
            if (target is not null && existing.TargetDate is null)
            {
                await _projects.SetTargetDateAsync(existing.Id, target, ct);
            }

            return existing.Id;
        }

        var created = await _projects.CreateAsync(name, target, ct);
        return created.Id;
    }

    private async Task<(ActionOutcome, Item?)> SetStatusAsync(AiAction action, CancellationToken ct)
    {
        if (action.Id is null)
        {
            return (Fail("set_status", "没给是哪一条。"), null);
        }

        var status = ParseStatus(action.Status);
        if (status is null)
        {
            return (Fail("set_status",
                $"状态「{action.Status}」不认识。只能用：pending / in_progress / done / skipped / abandoned。"), null);
        }

        var updated = await _items.SetStatusAsync(action.Id.Value, status.Value, action.Reason, ct);
        if (updated is null)
        {
            return (Fail("set_status", $"找不到这条记录（{action.Id}），可能已经被删了。"), null);
        }

        return (Ok("set_status", updated.Id, updated.Title, $"改成 {StatusName(status.Value)}"), updated);
    }

    private async Task<(ActionOutcome, Item?)> RescheduleAsync(AiAction action, CancellationToken ct)
    {
        if (action.Id is null)
        {
            return (Fail("reschedule", "没给是哪一条。"), null);
        }

        var day = ParseDate(action.Date);
        if (day is null)
        {
            return (Fail("reschedule", $"日期「{action.Date}」看不懂，要写成 yyyy-MM-dd。"), null);
        }

        var updated = await _items.RescheduleAsync(action.Id.Value, day.Value, ParseTime(action.Time), ct);
        if (updated is null)
        {
            return (Fail("reschedule", $"找不到这条记录（{action.Id}）。"), null);
        }

        return (Ok("reschedule", updated.Id, updated.Title, $"挪到 {day:MM-dd}"), updated);
    }

    private async Task<(ActionOutcome, Item?)> RenameAsync(AiAction action, CancellationToken ct)
    {
        if (action.Id is null || string.IsNullOrWhiteSpace(action.Title))
        {
            return (Fail("rename", "要给出是哪一条，以及改成什么。"), null);
        }

        var updated = await _items.RenameAsync(action.Id.Value, action.Title, ct);
        if (updated is null)
        {
            return (Fail("rename", $"找不到这条记录（{action.Id}）。"), null);
        }

        return (Ok("rename", updated.Id, updated.Title, "改了标题"), updated);
    }

    private async Task<(ActionOutcome, Item?)> DeleteAsync(AiAction action, CancellationToken ct)
    {
        if (action.Id is null)
        {
            return (Fail("delete", "没给是哪一条。"), null);
        }

        var target = await FindAsync(action.Id.Value, ct);
        if (target is null)
        {
            return (Fail("delete", $"找不到这条记录（{action.Id}）。"), null);
        }

        await _items.SoftDeleteAsync(action.Id.Value, ct);

        // 软删之后再取一次，让快照里带上墓碑（IsDeleted=true），
        // 这样撤销"删除"才知道该把墓碑清掉。
        await using (var db = await _dbFactory.CreateDbContextAsync(ct))
        {
            var deleted = await db.Items.IgnoreQueryFilters().FirstOrDefaultAsync(x => x.Id == action.Id.Value, ct);
            return (Ok("delete", target.Id, target.Title, "已删除（可撤销）"), deleted);
        }
    }

    private async Task<Item?> FindAsync(Guid id, CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        return await db.Items.FirstOrDefaultAsync(x => x.Id == id, ct);
    }

    private static DateOnly? ParseDate(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        // 明确指定格式，避免把 "2026/10/27" 和 "10-27-2026" 混着解析出不同结果。
        return DateOnly.TryParseExact(text.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None,
            out var d)
            ? d
            : null;
    }

    private static TimeSpan? ParseTime(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        return TimeSpan.TryParseExact(text.Trim(), "HH:mm", CultureInfo.InvariantCulture, out var t) ? t : null;
    }

    private static ItemStatus? ParseStatus(string? text)
    {
        return text?.Trim().ToLowerInvariant() switch
        {
            "pending" => ItemStatus.Pending,
            "in_progress" => ItemStatus.InProgress,
            "inprogress" => ItemStatus.InProgress,
            "done" => ItemStatus.Done,
            "skipped" => ItemStatus.Skipped,
            "abandoned" => ItemStatus.Abandoned,
            _ => null
        };
    }

    private static string StatusName(ItemStatus status) => status switch
    {
        ItemStatus.Pending => "待办",
        ItemStatus.InProgress => "进行中",
        ItemStatus.Done => "完成",
        ItemStatus.Skipped => "今天跳过",
        ItemStatus.Abandoned => "不做了",
        _ => status.ToString()
    };

    private static ActionOutcome Ok(string op, Guid id, string title, string detail) => new()
    {
        Op = op, Ok = true, Id = id, Title = title, Detail = detail
    };

    private static ActionOutcome Fail(string op, string detail) => new()
    {
        Op = op, Ok = false, Detail = detail
    };

    private static ActionType ToActionType(string? op) => op?.Trim().ToLowerInvariant() switch
    {
        "create" => ActionType.Create,
        "set_status" => ActionType.StatusChange,
        "delete" => ActionType.SoftDelete,
        _ => ActionType.Update
    };
}
