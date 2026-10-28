using DayManager.Core.Entities;
using DayManager.Core.Enums;
using Microsoft.EntityFrameworkCore;

namespace DayManager.Data.Repositories;

/// <summary>
/// 每个应用算什么（娱乐 / 工作 / 没标记）的读写。
///
/// 【为什么读的时候要带一份内置名单，而不是只返回库里的行】
/// 库里只有"用户手动标过的"那些。一个刚装上的游戏在库里没有任何行，
/// 而它恰恰是最该被拦的东西——用户不会先去设置页登记一遍再开始玩。
/// 所以判定顺序是：库里有明确标记就用它，没有就问内置名单，都没有就不干预。
/// 这份名单放在 <see cref="DayManager.Core.Intervention.EntertainmentApps"/>，
/// 和判定器在一起，方便被冒烟测试直接验证。
/// </summary>
public class AppRuleRepository
{
    private readonly IDbContextFactory<AppDbContext> _dbFactory;

    public AppRuleRepository(IDbContextFactory<AppDbContext> dbFactory)
    {
        _dbFactory = dbFactory;
    }

    /// <summary>所有手动标记过的应用。</summary>
    public async Task<List<AppRule>> GetAllAsync(CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        return await db.AppRules.AsNoTracking().ToListAsync(ct);
    }

    /// <summary>
    /// 查一个应用的标记。没标过返回 null（不是返回 Neutral）——
    /// 两者意义不同：null 表示"从未表态"，可以问名单；Neutral 表示"用户说过不用管"。
    ///
    /// 【为什么这条路径要单独提供，而不是让调用方 GetAll 再找】
    /// 干预引擎每 60 秒查一次，只关心当前这一个应用。
    /// 每次都把整张表读进内存再找，数据量小的时候没问题，
    /// 但那是"因为现在数据少所以可以浪费"的写法，改起来要动调用方。
    /// </summary>
    public async Task<AppRule?> GetAsync(string appId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(appId))
        {
            return null;
        }

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        return await db.AppRules.AsNoTracking()
            .FirstOrDefaultAsync(x => x.AppId == appId, ct);
    }

    /// <summary>
    /// 设置一个应用的归类（没有就新建）。
    ///
    /// 【为什么 AppName 也要一起写】
    /// 设置页上要让用户看清改的是哪个应用。进程名（chrome.exe）不够直观，
    /// 而采集记录里的显示名是现成的，顺手存一份，省得设置页再去关联查询。
    /// </summary>
    public async Task SetKindAsync(string appId, string appName, AppKind kind, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(appId))
        {
            throw new ArgumentException("应用标识不能为空", nameof(appId));
        }

        await using var db = await _dbFactory.CreateDbContextAsync(ct);

        var rule = await db.AppRules.FirstOrDefaultAsync(x => x.AppId == appId, ct);

        if (rule is null)
        {
            rule = new AppRule
            {
                Id = Guid.NewGuid(),
                AppId = appId,
                AppName = string.IsNullOrWhiteSpace(appName) ? appId : appName,
                Kind = kind,
                CreatedAt = DateTimeOffset.Now,
                UpdatedAt = DateTimeOffset.Now,
                Version = 1
            };

            db.AppRules.Add(rule);
        }
        else
        {
            rule.Kind = kind;

            // 名字跟着更新：有些应用升级后会改描述文字，留着旧的会让人对不上号。
            if (!string.IsNullOrWhiteSpace(appName))
            {
                rule.AppName = appName;
            }

            rule.UpdatedAt = DateTimeOffset.Now;
            rule.Version++;
        }

        await db.SaveChangesAsync(ct);
    }
}
