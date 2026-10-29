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

    /// <summary>
    /// 界面深浅。"system"（跟随系统）/ "light" / "dark"，默认 system。
    ///
    /// 【为什么非要有"强制深浅"这一档，只跟随系统不够】
    /// 典型的一台机器：系统是浅色（很多人从来没改过），
    /// 但编辑器、浏览器、终端全都被他自己设成了深色。
    /// 这时候一个"跟随系统"的软件会变成桌面上唯一一块白的——
    /// 尤其晚上，点开它像开了一盏灯。
    /// 系统主题表达的是"系统默认什么样"，不是"这个人此刻想看什么样"，
    /// 所以这个开关必须存在，而且值要存下来（重启后还按用户选的来）。
    /// </summary>
    public const string AppTheme = "app.theme";

    /// <summary>
    /// AI 说话的语气档。"reserved"（默认）/ "warm" / "direct"，取值见 <see cref="Core.Ai.AiTone"/>。
    ///
    /// 【为什么要能改，而不是我们替他定死一种】
    /// "AI 该怎么跟我说话"是口味问题，而口味会变：
    /// 某段时间想听两句人话，另一段时间一句多余的话都是骚扰。
    /// 定死一种等于替他做会反悔的决定，所以他自己在时间页切，切完下一次对话就生效。
    /// </summary>
    public const string AiTone = "ai.tone";

    /// <summary>
    /// 他自己写的一句话人设（可空）。比如"像个一起熬夜的朋友，别叫我加油"。
    ///
    /// 【为什么除了三档还要留一个能自己写的地方】
    /// 三档是我们猜的，猜不全。而"我希望它怎么跟我说话"这件事，
    /// 每个人心里那句话都不一样，让他自己写比我们再猜十档有用。
    ///
    /// 【为什么它是补充而不是覆盖】
    /// 这句话插在语气档之后、硬约束之前，能改说话的分寸，
    /// 但改不动输出格式和不许编造这类规矩——那些排在它后面，对模型的约束力更强。
    /// 他要的是调口味，不是让 AI 不干活。
    /// </summary>
    public const string AiPersona = "ai.persona";

    /// <summary>AI 主动开口的总开关。"true" / "false"，默认开。</summary>
    public const string ProactiveEnabled = "proactive.enabled";

    /// <summary>
    /// 某一种场合今天已经说过了。后面接场合名 + 日期。
    ///
    /// 【为什么要按日期存而不是只存"说过没有"】
    /// 明天要重新说。只存布尔值的话，第二天早晨它还是"说过了"，
    /// 于是早晨计划这个功能只在第一天生效过一次。
    /// </summary>
    public const string ProactiveFiredPrefix = "proactive.fired.";

    /// <summary>
    /// 某一条日程已经提醒过了。后面接条目 id。
    ///
    /// 【为什么不按日期】同一条日程不会天天有，按条目记一次就够；
    /// 记成日期反而是给数据库添无限增长的垃圾。
    /// </summary>
    public const string ProactiveBeforePrefix = "proactive.before.";

    /// <summary>上一次开口的时刻（yyyy-MM-dd HH:mm），用来做冷却。</summary>
    public const string ProactiveLastAt = "proactive.last_at";

    /// <summary>
    /// 点过通知上「今天别再提了」的那一天（yyyy-MM-dd）。
    ///
    /// 【为什么主动开口也要有赦免权】
    /// 它比干预提醒更容易招人烦——干预至少是在你真的刷了半小时之后才响，
    /// 而这个是它自己找上门。没有一次性关掉今天的办法，
    /// 用户关掉的就是整个软件。
    /// </summary>
    public const string ProactiveSnoozed = "proactive.snoozed";

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
