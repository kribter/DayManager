using DayManager.Core.Entities;
using DayManager.Core.Enums;
using DayManager.Data.Snapshots;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;

namespace DayManager.Data.Repositories;

/// <summary>
/// 写操作日志与撤销。
///
/// 【这一层存在的唯一理由】
/// AI 会犯错，而且它的错是批量的。决策 D4 定了"永久可撤、入口在界面上、不经过 AI"，
/// 这里就是那句话落地的地方。
///
/// 【为什么撤销要按批次而不是按条】
/// AI 的一次动作往往同时改好几条（"把这五条都推到下周"）。
/// 按条撤的话，撤一条留四条，剩下的是半套计划——比完全不撤更混乱。
/// 所以同一批共享一个 BatchId，撤销以批为单位，需要时也能展开逐条撤。
///
/// 【为什么撤销前要检测冲突】
/// 从记下日志到现在，中间可能已经发生别的变化（你自己改过、AI 又改过）。
/// 这时闷头把旧快照写回去，会把别人的（或自己的）后续修改悄悄抹掉——
/// 这正是"撤销"最容易变成"破坏"的地方。检测到不一致，先说清楚再让你决定。
/// </summary>
public class ActionLogRepository
{
    private readonly IDbContextFactory<AppDbContext> _dbFactory;

    private static readonly JsonSerializerOptions JsonOptions = new();

    public ActionLogRepository(IDbContextFactory<AppDbContext> dbFactory)
    {
        _dbFactory = dbFactory;
    }

    /// <summary>一次操作里被碰到的某一条记录。</summary>
    public sealed record ActionEntry(
        ActionType Action,
        Guid TargetId,
        string? BeforeJson,
        string? AfterJson,
        string TargetType = "items");

    /// <summary>
    /// 记录一批操作。返回批次号，撤销时用它。
    ///
    /// 【为什么要一次写入整批，而不是每改一条写一条】
    /// 分多次写的话，中途失败会留下半批日志，撤销时就只能撤一半。
    /// 一次提交要么整批都在，要么整批都不在。
    /// </summary>
    public async Task<Guid> LogBatchAsync(ActionActor actor, IEnumerable<ActionEntry> entries, string summary,
        CancellationToken ct = default)
    {
        var batchId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        await using var db = await _dbFactory.CreateDbContextAsync(ct);

        foreach (var e in entries)
        {
            db.ActionLogs.Add(new ActionLog
            {
                BatchId = batchId,
                Actor = actor,
                Action = e.Action,
                TargetType = e.TargetType,
                TargetId = e.TargetId,
                BeforeJson = e.BeforeJson,
                AfterJson = e.AfterJson,
                Summary = summary,
                CreatedAt = now
            });
        }

        await db.SaveChangesAsync(ct);
        return batchId;
    }

    /// <summary>
    /// 记录一条 items 的变更（便捷方法，内部会把实体转成快照）。
    /// </summary>
    public async Task<Guid> LogItemChangeAsync(ActionActor actor, ActionType action, Item before, Item after,
        string summary, CancellationToken ct = default)
    {
        return await LogBatchAsync(actor, new[]
        {
            new ActionEntry(
                action,
                after.Id,
                JsonSerializer.Serialize(ItemSnapshot.From(before), JsonOptions),
                JsonSerializer.Serialize(ItemSnapshot.From(after), JsonOptions))
        }, summary, ct);
    }

    /// <summary>界面上"最近可撤销"列表要的一行。</summary>
    public sealed record UndoBatch
    {
        public Guid BatchId { get; init; }
        public string Summary { get; init; } = string.Empty;
        public DateTimeOffset CreatedAt { get; init; }
        public int EntryCount { get; init; }
        public bool IsAi { get; init; }

        /// <summary>给人看的一行摘要，例如"AI · 12 分钟前 · 改了 5 条"。</summary>
        public string DisplayText
        {
            get
            {
                var minutes = (int)(DateTimeOffset.Now - CreatedAt.ToLocalTime()).TotalMinutes;
                var when = minutes < 1 ? "刚刚" : minutes < 60 ? $"{minutes} 分钟前" : $"{minutes / 60} 小时前";
                return $"{(IsAi ? "AI" : "你")} · {when} · {Summary}（{EntryCount} 条）";
            }
        }
    }

    /// <summary>
    /// 列出最近可撤销的批次。
    ///
    /// 【为什么只列 AI 的】
    /// 决策 D4：撤销只覆盖 AI 的操作。用户自己的单条操作一步就能改回来，
    /// 把它们也列进来只会让这个清单变长、变吵，真正需要撤的那条反而更难找。
    /// </summary>
    public async Task<List<UndoBatch>> GetUndoableBatchesAsync(int limit = 20, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);

        var rows = await db.ActionLogs
            .Where(x => x.UndoneAt == null && x.Actor == ActionActor.Ai)
            .ToListAsync(ct);

        return rows
            .GroupBy(x => x.BatchId)
            .Select(g => new UndoBatch
            {
                BatchId = g.Key,
                Summary = g.First().Summary,
                CreatedAt = g.Min(x => x.CreatedAt),
                EntryCount = g.Count(),
                IsAi = g.First().Actor == ActionActor.Ai
            })
            .OrderByDescending(x => x.CreatedAt)
            .Take(limit)
            .ToList();
    }

    /// <summary>撤销的结果。有冲突时必须说清楚是哪些、为什么，不能只回一个 bool。</summary>
    public sealed record UndoResult
    {
        /// <summary>实际撤销了几条。</summary>
        public int UndoneCount { get; init; }

        /// <summary>
        /// 冲突描述。非空表示有记录在这之后被改过，需要用户确认后才能继续。
        /// </summary>
        public List<string> Conflicts { get; init; } = new();

        /// <summary>一句话结果，直接显示给用户。</summary>
        public string Message { get; init; } = string.Empty;

        public bool HasConflicts => Conflicts.Count > 0;
        public bool Success => UndoneCount > 0 && !HasConflicts;
    }

    /// <summary>
    /// 撤销一批操作。
    /// </summary>
    /// <param name="force">
    /// 强制撤销：检测到冲突也照样覆盖。
    /// 【为什么要有这个开关而不是直接撤】
    /// 冲突意味着"有人在这之后动过它"。默认不覆盖，把情况说给用户听，
    /// 由他决定是"就按当时的样子撤"还是算了。擅自覆盖是撤销功能最危险的形态。
    /// </param>
    public async Task<UndoResult> UndoBatchAsync(Guid batchId, bool force = false, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);

        var logs = await db.ActionLogs
            .Where(x => x.BatchId == batchId && x.UndoneAt == null)
            .OrderByDescending(x => x.CreatedAt)
            .ToListAsync(ct);

        if (logs.Count == 0)
        {
            return new UndoResult { Message = "这一批已经撤过了，或者找不到记录。" };
        }

        var conflicts = new List<string>();
        var undone = 0;
        var now = DateTimeOffset.UtcNow;

        // 【为什么要倒着撤】
        // 后发生的操作建立在先发生的结果之上。正着撤的话，
        // 先撤第一条把状态改回旧值，再撤第二条时它拿的"改之前"是错的。
        // 倒序撤销保证每一步面对的现场都和操作发生时一致。
        foreach (var log in logs)
        {
            if (await ApplyUndoAsync(db, log, force, now, conflicts, ct))
            {
                undone++;
            }
        }

        if (conflicts.Count == 0)
        {
            await db.SaveChangesAsync(ct);
        }

        return new UndoResult
        {
            UndoneCount = undone,
            Conflicts = conflicts,
            Message = conflicts.Count == 0
                ? $"已撤销 {undone} 条改动。"
                : $"有 {conflicts.Count} 条在这之后被改过，没有动它们。确认后可以选择强制撤销。"
        };
    }

    /// <summary>
    /// 撤销批次里的**单条**（把一批展开后，只撤其中一条时用）。
    ///
    /// 【为什么不复用 UndoBatchAsync】
    /// 那个方法会把整批都撤掉。用户在展开列表里点某一条，
    /// 意图是"只撤这个"，顺手把其余九条也撤了是严重的误解——
    /// 而他甚至不会察觉，因为界面上会显示"已撤销 10 条"。
    /// </summary>
    public async Task<UndoResult> UndoOneAsync(Guid logId, bool force = false, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);

        var log = await db.ActionLogs.FirstOrDefaultAsync(x => x.Id == logId, ct);
        if (log is null)
        {
            return new UndoResult { Message = "找不到这条记录。" };
        }

        if (log.UndoneAt != null)
        {
            return new UndoResult { Message = "这条已经撤过了。" };
        }

        var conflicts = new List<string>();
        var ok = await ApplyUndoAsync(db, log, force, DateTimeOffset.UtcNow, conflicts, ct);

        if (conflicts.Count == 0)
        {
            await db.SaveChangesAsync(ct);
        }

        return new UndoResult
        {
            UndoneCount = ok ? 1 : 0,
            Conflicts = conflicts,
            Message = conflicts.Count == 0
                ? "已撤销这一条。"
                : conflicts[0] + " 确认后可以选择强制撤销。"
        };
    }

    /// <summary>
    /// 真正执行一条日志的撤销。返回是否成功撤销。
    ///
    /// 【为什么冲突时不直接撤，而要先返回】
    /// 冲突意味着这条记录在操作之后又被改过（被你自己，或被 AI 的另一次操作）。
    /// 直接覆盖会悄悄抹掉后来的改动，那不是撤销，是破坏。
    /// 所以先把情况收集起来交给调用方，由用户决定是否 force。
    /// </summary>
    private static async Task<bool> ApplyUndoAsync(
        AppDbContext db,
        ActionLog log,
        bool force,
        DateTimeOffset now,
        List<string> conflicts,
        CancellationToken ct)
    {
        if (log.TargetType != "items")
        {
            // 一期只有 items 支持真正撤销。其余类型静默跳过而不计入成功数，
            // 是为了不在界面上假装"撤销成功"——那比不做更糟。
            return false;
        }

        // 被软删的记录也要能找出来（撤销"删除"就是把它救回来）
        var item = await db.Items
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(x => x.Id == log.TargetId, ct);

        if (item is null)
        {
            conflicts.Add($"有一条记录找不到了（可能已被彻底删除）：{log.Summary}");
            return false;
        }

        // 冲突检测：现在的样子，和操作刚做完时还一样吗？
        // 比较的是业务字段快照（ItemSnapshot 不含同步元数据），
        // 否则 Version、UpdatedAt 每次保存都变，会永远判定为冲突。
        if (!force && log.AfterJson != null)
        {
            var current = JsonSerializer.Serialize(ItemSnapshot.From(item), JsonOptions);
            if (current != log.AfterJson)
            {
                conflicts.Add($"「{item.Title}」在这之后又被改过了，撤销会覆盖掉后来的改动。");
                return false;
            }
        }

        if (log.BeforeJson is null)
        {
            // 没有"之前" → 这条是被新建出来的，撤销就是把它删掉（软删，留墓碑）
            item.DeletedAt ??= DateTimeOffset.Now;
        }
        else
        {
            var snapshot = JsonSerializer.Deserialize<ItemSnapshot>(log.BeforeJson, JsonOptions);
            if (snapshot is null)
            {
                conflicts.Add($"「{item.Title}」的历史记录读不出来，没法撤销。");
                return false;
            }

            snapshot.ApplyTo(item);
        }

        log.UndoneAt = now;
        return true;
    }
}
