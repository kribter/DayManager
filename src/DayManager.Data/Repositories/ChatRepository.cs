using DayManager.Core.Entities;
using Microsoft.EntityFrameworkCore;

namespace DayManager.Data.Repositories;

/// <summary>
/// 对话记录的读写。
///
/// 【为什么这一层的接口这么少】
/// 对话只有三个真实需求：打开时把最近的捞出来、说话时写进去、撤回时改个标记。
/// 加"按日期查""按关键词搜"这类接口现在一个都用不上，
/// 写了就是等着以后改的时候一起改错。
/// </summary>
public class ChatRepository
{
    private readonly IDbContextFactory<AppDbContext> _factory;

    public ChatRepository(IDbContextFactory<AppDbContext> factory)
    {
        _factory = factory;
    }

    /// <summary>
    /// 最近若干条，**按时间正序**返回（界面从上往下读）。
    ///
    /// 【为什么要先倒序取再反过来，而不是正序取最后 N 条】
    /// 正序取最后 N 条要先跳过前面的所有行，聊了三个月之后就是全表扫。
    /// 倒序取 N 条走的是 CreatedAt 索引，然后只在内存里翻转这个小列表。
    ///
    /// 【为什么按 Id 做第二排序】
    /// 同一毫秒里可能存进两条（用户发一句、AI 回一句几乎同时写完时）
    /// 只按时间排的话顺序不确定，界面上表现为"偶尔上下颠倒"。
    /// 加一个稳定的第二排序键就不会。
    /// </summary>
    public async Task<List<ChatMessage>> GetRecentAsync(int count, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);

        var list = await db.ChatMessages
            .Where(x => x.DeletedAt == null)
            .OrderByDescending(x => x.CreatedAt)
            .ThenByDescending(x => x.Id)
            .Take(count)
            .ToListAsync(ct);

        list.Reverse();
        return list;
    }

    /// <summary>写一条进去。返回存好的实体（带上了自动填的时间）。</summary>
    public async Task<ChatMessage> AddAsync(
        string role,
        string text,
        Guid? batchId = null,
        string? outcomesJson = null,
        CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);

        var message = new ChatMessage
        {
            Role = role,
            Text = text,
            BatchId = batchId,
            OutcomesJson = outcomesJson
        };

        db.ChatMessages.Add(message);
        await db.SaveChangesAsync(ct);

        return message;
    }

    /// <summary>
    /// 把某一批标记成"已撤回"。
    ///
    /// 【为什么改的是对话记录而不是 action_log】
    /// action_log 是事实日志，它只该记录"发生过什么"，不该被改写。
    /// "这一批已经被用户撤回了"是**界面的状态**，属于对话这一侧。
    /// </summary>
    public async Task MarkUndoneAsync(Guid batchId, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);

        var rows = await db.ChatMessages
            .Where(x => x.BatchId == batchId)
            .ToListAsync(ct);

        foreach (var row in rows)
        {
            row.IsUndone = true;
        }

        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// 清空对话历史（软删）。
    ///
    /// 【为什么是软删】
    /// 误点了清空还能捞回来；而且别的表都是软删，这里硬删会破坏一致性。
    /// </summary>
    public async Task<int> ClearAsync(CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);

        var rows = await db.ChatMessages
            .Where(x => x.DeletedAt == null)
            .ToListAsync(ct);

        var now = DateTimeOffset.UtcNow;
        foreach (var row in rows)
        {
            row.DeletedAt = now;
        }

        return await db.SaveChangesAsync(ct);
    }
}
