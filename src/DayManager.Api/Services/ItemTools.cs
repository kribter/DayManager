using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
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

            case "query":
                // 【为什么这里不查，而是明确报错】
                // query 是只读动作，由 AgentService 消化掉，根本不该传到这一层
                // （这一层每走一步都要记 before/after 快照、进撤销批次）。
                // 真漏到这里，说明上面过滤掉了但没滤干净——静默忽略会让排查的人
                // 以为"查过了其实没查"，所以给一句指着源头的报错。
                return (Fail("query", "query 是只读查询，应由 AgentService 处理，不走执行器。"), null);

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

        // 【只有截止日的事走哪条路】
        // 用户说「12-15 交」而没说哪天动手 → AI 给 dueDate，不拆、不摊派、不落到某一天。
        // 这里刻意不猜：dueDate 写了却解析不了，就明确报错让它重给，
        // 绝不悄悄退化成「加到今天」——那会把一件悬着的事变成今天的欠账。
        var dueDay = ParseDate(action.DueDate);
        if (!string.IsNullOrWhiteSpace(action.DueDate) && dueDay is null)
        {
            return (Fail("create", $"截止日「{action.DueDate}」看不懂，要写成 yyyy-MM-dd。"), null);
        }

        // 「只有截止日」和「具体几点」是互斥的：给了钟点就说明它已经
        // 被安排在某一刻开始，不再属于「中间哪天做都行」那一类。
        // 同时给两样，说明模型没分清两种形态，报错比猜一个更不容易写坏数据。
        if (dueDay is not null && !string.IsNullOrWhiteSpace(action.Time))
        {
            return (Fail("create", "只有截止日的事不要再给 time；要定几点就改用 date + time。"), null);
        }

        var day = ParseDate(action.Date) ?? DateOnly.FromDateTime(DateTime.Now);
        var time = ParseTime(action.Time);

        // 阶段性目标不能同时是「只有截止日」：一个是不属于任何一天的壳，
        // 一个是挂在整段上的事，形态冲突。报错比猜一个更不容易写坏数据。
        if (action.IsStage == true && dueDay is not null)
        {
            return (Fail("create", "阶段性目标是容器，别同时给 dueDate；要定阶段截止日就用 date。"), null);
        }

        // 父任务按标题找，找不到明确报错。「不猜」是执行器的硬规矩：
        // 猜一个相近的标题，会把子任务挂到无关的事下面，而且界面上看不出来。
        Guid? parentTaskId = null;
        if (!string.IsNullOrWhiteSpace(action.ParentTitle))
        {
            parentTaskId = await ResolveParentTaskAsync(action.ParentTitle.Trim(), ct);
            if (parentTaskId is null)
            {
                return (Fail("create", $"找不到叫「{action.ParentTitle.Trim()}」的阶段性目标；先在同一批里建它，再挂子项。"), null);
            }
        }

        // 挂项目：同一件事拆出来的几条写同一个名字，就会归到同一个项目下。
        Guid? projectId = null;
        if (!string.IsNullOrWhiteSpace(action.Project))
        {
            projectId = await ResolveProjectAsync(
                action.Project.Trim(), ParseDate(action.ProjectTarget), ct);
        }

        // 【两种完成方式开关只给正经待办】
        // 定段（只有截止日）本来就不进分母、容器由子项汇总，各有自己的语义；
        // 混着给说明模型没分清，报错比悄悄忽略更容易被发现、也更不容易写坏数据。
        if ((action.RepeatsDaily == true || action.NoVerdict == true)
            && (action.IsStage == true || dueDay is not null))
        {
            return (Fail("create",
                "repeatsDaily / noVerdict 只能给普通待办；定段和容器不用这两个开关。"), null);
        }

        // 三条创建路径，形态各由入口写死，不让模型自己填：
        //   isStage        → 容器（ByChildren，不进今日清单 / 分母）
        //   dueDate        → 定段（只有截止日，常驻带）
        //   date / time    → 定日 / 定点（正经待办，可再叠两个完成方式开关）
        Item item;
        if (action.IsStage == true)
        {
            item = await _items.AddContainerAsync(action.Title.Trim(),
                ParseDate(action.Date), parentTaskId, projectId, ct);
        }
        else if (dueDay is not null)
        {
            item = await _items.AddDeadlineAsync(action.Title.Trim(), dueDay.Value, ct);
        }
        else
        {
            item = await _items.AddToDayAsync(action.Title.Trim(), day, time, ct,
                completionMode: action.RepeatsDaily == true
                    ? CompletionMode.PerOccurrence : null,
                verdictMode: action.NoVerdict == true
                    ? VerdictMode.Never : null,
                parentTaskId: parentTaskId);
        }

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

        //
        // 【为什么时间要用 HH 而不是 hh】
        // hh 是 12 小时制：15:00 会被显示成 "03:00"，下午的日程看着像凌晨三点。
        // 这里是给用户看的结果文字，不是给机器读的，必须保留 24 小时制。
        // 另外前面要留一个空格——贴着日期写成 "10-3015:00" 看不出来那是几点。
        //
        // 【结果文案也要把两种形态说开】
        // 这句话会回喂给模型（动作执行结果），它照着写 reply。
        // 要是截止日的事也说「加到 12-15」，模型下一句就会讲成
        // 「已经安排在 15 号了」——用户以为那天才需要面对，正是要防的误会。
        // 回喂给模型的这句要是漏了开关，它下一句 reply 就会把每日任务说成
        // 「已安排今天」、把不判成败的说成「今天该做完」——等于白建。
        var what = action.IsStage == true
            ? "建了一个阶段性目标（容器，进度按子项汇总）"
            : dueDay is not null
                ? $"{dueDay.Value:MM-dd} 截止，之前一直挂着、不算今天该做完"
                : $"加到 {day:MM-dd}" + (time is null ? "" : " " + time.Value.ToString(@"HH\:mm"))
                  + (action.RepeatsDaily == true ? "（每次各算一次，漏做当天封账）" : "")
                  + (action.NoVerdict == true ? "（不判成败，不算今天该做完）" : "");

        return (Ok("create", item.Id, item.Title,
            what
            + (action.ParentTitle is null ? "" : $"，挂在「{action.ParentTitle.Trim()}」下")
            + (action.Project is null ? "" : $"，归到「{action.Project.Trim()}」")), item);
    }

    /// <summary>
    /// 按标题找阶段性目标（容器）。只认 CompletionMode=ByChildren 的记录，
    /// 免得把一条普通待办当成父项，把层级挂歪。同名取最近建的那条。
    ///
    /// 【为什么按名字而不是 id】
    /// AI 一次输出的 actions 是平铺的，它看不到系统刚生成的 Guid。
    /// 让它用标题引用同批里先建的那条，是唯一不需要额外协议的办法；
    /// 执行器按数组顺序跑，先建的容器此时已落库，查得到。
    /// </summary>
    private async Task<Guid?> ResolveParentTaskAsync(string title, CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);

        return await db.Items
            .Where(x => x.Title == title
                        && x.CompletionMode == CompletionMode.ByChildren
                        && x.Status != DayManager.Core.Enums.ItemStatus.Abandoned)
            .OrderByDescending(x => x.CreatedAt)
            .Select(x => (Guid?)x.Id)
            .FirstOrDefaultAsync(ct);
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

    /// <summary>
    /// 解析具体时刻。认得的标准写法以外的，能接住的尽量接住。
    ///
    /// 【为什么这里要收这么多种写法】
    /// 模型被要求输出 HH:mm，但它不是机器：单数字的 "9:30"、带秒的 "15:00:00"、
    /// 偶尔还会照着用户的原话写 "下午三点"。
    /// 每漏掉一种，后果都是同一件最糟的事：
    /// 用户明确说了几点，建出来的那条却没有时间，而且没有任何报错。
    /// 静默丢字段是这个软件里最难发现、也最难信任回来的一类错误，
    /// 宁可在这里多写几行把它接住。
    /// </summary>
    private static TimeSpan? ParseTime(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var raw = text.Trim();

        // 先用明确格式，避免把 "10-27" 这类东西也糊里胡嗦解析成时间。
        if (TimeSpan.TryParseExact(raw, new[] { "HH:mm", "H:mm", "HH:mm:ss", "H:mm:ss" },
                CultureInfo.InvariantCulture, out var exact))
        {
            return exact;
        }

        if (TimeSpan.TryParse(raw, CultureInfo.InvariantCulture, out var loose))
        {
            return loose;
        }

        // 最后试中文口语："下午三点" "晚上十点半" "3点15"。
        return ParseSpokenTime(raw);
    }

    /// <summary>
    /// 中文口语时刻 → TimeSpan。认不全也没关系，认不出来返回 null，
    /// 由调用方照旧建成不带时刻的待办（这条用户可以自己再改，不会写坏数据）。
    /// </summary>
    private static TimeSpan? ParseSpokenTime(string text)
    {
        var hourMatch = Regex.Match(text, @"(\d{1,2}|[一二三四五六七八九十两]+)\s*[点时]");
        if (!hourMatch.Success)
        {
            return null;
        }

        var hourText = hourMatch.Groups[1].Value;
        var hour = int.TryParse(hourText, out var n) ? n : SpokenNumber(hourText);
        if (hour is < 0 or > 23)
        {
            return null;
        }

        // 分钟：只有紧跟在"点"后面的那几个字才算，避免把句子后半段的数字也吃进来。
        var tail = text[(hourMatch.Index + hourMatch.Length)..];
        var minute = 0;

        if (tail.Contains('半'))
        {
            minute = 30;
        }
        else if (tail.Contains("一刻"))
        {
            minute = 15;
        }
        else
        {
            var minuteMatch = Regex.Match(tail, @"(\d{1,2})\s*分");
            if (minuteMatch.Success)
            {
                minute = int.Parse(minuteMatch.Groups[1].Value);
            }
        }

        // 下午/晚上/傍晚/夜里 +12。上午/早上/凌晨保持原样。
        // 已经大于 12 的说明它自己带了 24 小时制的数（"下午15点"），不再加。
        foreach (var (word, shift) in MeridiemWords)
        {
            if (shift > 0 && hour < 12 && text.Contains(word))
            {
                hour += shift;
                break;
            }
        }

        return hour > 23 || minute > 59 ? null : new TimeSpan(hour, minute, 0);
    }

    /// <summary>口语里表示时段的词，以及对应的小时偏移。</summary>
    private static readonly (string Word, int Shift)[] MeridiemWords =
    {
        ("下午", 12), ("傍晚", 12), ("晚上", 12), ("夜里", 12), ("半夜", 0),
        ("中午", 0), ("上午", 0), ("早上", 0), ("凌晨", 0)
    };

    private static int SpokenNumber(string text) => text switch
    {
        "一" => 1, "两" => 2, "二" => 2, "三" => 3, "四" => 4, "五" => 5,
        "六" => 6, "七" => 7, "八" => 8, "九" => 9, "十" => 10,
        "十一" => 11, "十二" => 12,
        _ => -1
    };

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

    private static string StatusName(ItemStatus status) => status.ToDisplayName();

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
