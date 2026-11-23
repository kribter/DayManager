using DayManager.Core.Entities;
using Microsoft.EntityFrameworkCore;

namespace DayManager.Data.Repositories;

/// <summary>
/// AI 记忆（语义条目）的读写。画像存数字、记忆存话——"管家记得你"的落点。
/// </summary>
public class MemoryRepository
{
    private readonly IDbContextFactory<AppDbContext> _factory;

    public MemoryRepository(IDbContextFactory<AppDbContext> factory)
    {
        _factory = factory;
    }

    /// <summary>落一条记忆。返回存好的实体（带自动填的时间）。</summary>
    public async Task<AiMemory> AddAsync(string content, string reason, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);

        var memory = new AiMemory { Content = content, Reason = reason };
        db.AiMemories.Add(memory);
        await db.SaveChangesAsync(ct);

        return memory;
    }

    /// <summary>取全部未删的记忆，按最近记的倒序。"筛"去重 / 顶掉时喂给模型用。</summary>
    public async Task<List<AiMemory>> GetAllAsync(CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);

        return await db.AiMemories
            .Where(x => x.DeletedAt == null)
            .OrderByDescending(x => x.CreatedAt)
            .ToListAsync(ct);
    }

    /// <summary>最近 N 条未删的，倒序。拼提示词用（取完再截断，占 token 可控）。</summary>
    public async Task<List<AiMemory>> GetRecentAsync(int count, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);

        return await db.AiMemories
            .Where(x => x.DeletedAt == null)
            .OrderByDescending(x => x.CreatedAt)
            .Take(count)
            .ToListAsync(ct);
    }

    /// <summary>改一条（用户纠正）。</summary>
    public async Task<bool> UpdateAsync(Guid id, string content, string reason, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);

        var row = await db.AiMemories.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (row is null)
        {
            return false;
        }

        row.Content = content;
        row.Reason = reason;
        await db.SaveChangesAsync(ct);
        return true;
    }

    /// <summary>删一条（软删）。</summary>
    public async Task<bool> DeleteAsync(Guid id, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);

        var row = await db.AiMemories.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (row is null)
        {
            return false;
        }

        row.DeletedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        return true;
    }
}
