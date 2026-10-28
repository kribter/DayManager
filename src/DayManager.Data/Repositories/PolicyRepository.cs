using DayManager.Core.Entities;
using DayManager.Core.Enums;
using Microsoft.EntityFrameworkCore;

namespace DayManager.Data.Repositories;

/// <summary>
/// 干预相关的参数名。
///
/// 【为什么键要集中成常量，而不是散在调用处写字符串】
/// 这些字符串同时被写入方（设置页）和读取方（干预引擎、AI 提示词）使用。
/// 散着写的话，一处拼错一个字符，表现是"设置改了但没生效"——
/// 不报错、不进日志，只有对着数据库看才能发现。
/// </summary>
public static class PolicyKeys
{
    /// <summary>干预总开关。"true" / "false"。</summary>
    public const string InterventionEnabled = "intervention.enabled";

    /// <summary>连续用多久开始提醒（分钟）。注意这是**同一段不中断的使用**，不是当天累计。</summary>
    public const string ThresholdMinutes = "intervention.threshold_minutes";

    /// <summary>同一个应用两次提醒之间至少隔多久（分钟）。</summary>
    public const string CooldownMinutes = "intervention.cooldown_minutes";

    /// <summary>点窗口的 X 时是"藏起来继续跑"还是"真的退出"。默认藏着。</summary>
    public const string CloseToTray = "app.close_to_tray";

    /// <summary>「今天别再提了」的前缀，后面接进程名。</summary>
    public const string SnoozePrefix = "intervention.snooze.";

    /// <summary>拼出某个应用的赦免键。</summary>
    public static string SnoozeFor(string appId)
    {
        // Key 列有 64 字符上限。进程名一般十几二十个字符，
        // 但"用户改过路径的绿色版"偶尔会给个长名字，截一下比让数据库报错强。
        var trimmed = appId.Length > 40 ? appId[..40] : appId;
        return SnoozePrefix + trimmed;
    }
}

/// <summary>
/// 干预参数的读写，以及"今天别再提了"的赦免记录。
///
/// 【为什么赦免也存这张表，而不是内存里放个集合】
/// 你说过想要的是"今天别再提了"，不是"本次运行别再提了"。
/// 只放内存的话，重启一次客户端它又开始提——而重启是很常见的事（编译、断电、随手关掉）。
/// 存库之后这条承诺才立得住。日期到了自然失效，不需要谁去清理。
/// </summary>
public class PolicyRepository
{
    private readonly IDbContextFactory<AppDbContext> _dbFactory;

    public PolicyRepository(IDbContextFactory<AppDbContext> dbFactory)
    {
        _dbFactory = dbFactory;
    }

    public async Task<string?> GetRawAsync(string key, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        return await db.PolicySettings.AsNoTracking()
            .Where(x => x.Key == key)
            .Select(x => x.Value)
            .FirstOrDefaultAsync(ct);
    }

    /// <summary>
    /// 读一个整数参数。没设过、或者存进去的东西解析不出来，都返回 fallback。
    ///
    /// 【为什么解析失败要悄悄退回默认值，而不是抛异常】
    /// 参数是给人调的（设置页、将来的 AI），写坏一次不该让整个干预功能停摆。
    /// 提醒晚一分钟可以接受，软件因为一个数字崩掉不可以。
    /// </summary>
    public async Task<int> GetIntAsync(string key, int fallback, CancellationToken ct = default)
    {
        var raw = await GetRawAsync(key, ct);
        return int.TryParse(raw, out var value) ? value : fallback;
    }

    public async Task<bool> GetBoolAsync(string key, bool fallback, CancellationToken ct = default)
    {
        var raw = await GetRawAsync(key, ct);
        return bool.TryParse(raw, out var value) ? value : fallback;
    }

    /// <summary>
    /// 写一个参数。第一次写入时顺便把默认值也记下来（"恢复默认"和校准基线都要用它）。
    /// </summary>
    public async Task SetAsync(
        string key,
        string value,
        PolicySource source,
        string? reason = null,
        CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);

        var setting = await db.PolicySettings.FirstOrDefaultAsync(x => x.Key == key, ct);

        if (setting is null)
        {
            db.PolicySettings.Add(new PolicySetting
            {
                Id = Guid.NewGuid(),
                Key = key,
                Value = value,
                // DefaultValue 记的是"出厂值"，不是"上一次的值"。
                // 第一次写入时两者相同，之后只更新 Value。
                DefaultValue = value,
                Source = source,
                Reason = reason,
                CreatedAt = DateTimeOffset.Now,
                UpdatedAt = DateTimeOffset.Now,
                Version = 1
            });
        }
        else
        {
            setting.Value = value;
            setting.Source = source;

            if (reason is not null)
            {
                setting.Reason = reason;
            }

            setting.UpdatedAt = DateTimeOffset.Now;
            setting.Version++;
        }

        await db.SaveChangesAsync(ct);
    }

    /// <summary>某个应用今天被赦免到了哪一天。没赦免过返回 null。</summary>
    public async Task<DateOnly?> GetSnoozeDateAsync(string appId, CancellationToken ct = default)
    {
        var raw = await GetRawAsync(PolicyKeys.SnoozeFor(appId), ct);

        return DateOnly.TryParse(raw, out var day) ? day : null;
    }

    /// <summary>记下"今天别再提这个应用了"。</summary>
    public Task SnoozeAsync(string appId, DateOnly day, CancellationToken ct = default) =>
        SetAsync(
            PolicyKeys.SnoozeFor(appId),
            day.ToString("yyyy-MM-dd"),
            PolicySource.User,
            "用户在这一天按了「今天别再提了」",
            ct);
}
