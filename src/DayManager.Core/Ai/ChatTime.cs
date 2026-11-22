namespace DayManager.Core.Ai;

/// <summary>
/// 把「时间」变成模型看得懂、不用自己推的字。
///
/// 【为什么会有这个文件】
/// 两条链路（聊天、主动开口）都把历史消息以**纯文本**塞给模型，
/// 系统提示里只留一个孤零零的「现在 HH:mm」。于是它不知道每条历史是几点说的、
/// 相邻两句隔了多久、上一句距现在过了几个小时。实测出来的事故：
///   - 上午 10:57 接着昨晚深夜的话说「晚安」（跨夜历史被当成连续一场）；
///   - 18:03 说「去吃饭了」，20:10 还在劝「饭先吃」（状态不会过期）；
///   - 让它自己减 23:49 到 0 点，答成「十点差十分」（心算时间差）。
/// 信息其实都在库里，只是**没被带过来**。
///
/// 【为什么不把它交给模型推】
/// 项目里立过的原则是：硬数字由代码算好，只喂结果，模型负责措辞和分寸。
/// 时间差、时段、日期归属都是硬数字，属于这一类。给原始时刻让它自己减，
/// 等于把一个没有强制步骤的隐性子任务塞进生成过程——它多半不会去算。
///
/// 【为什么放 Core 而不是 Api】
/// 客户端拼历史、后端两个 Service 都要用同一份口径；
/// 放 Core 还能让冒烟直接断言这些字符串，不用真调一次大模型。
/// 这类错误没有报错、没有日志，只能靠"读一遍算出来的字"发现。
///
/// 【这里不做的事】
/// 不改「一天从几点开始」的业务口径（跨午夜按凌晨 4 点切是另一件事）。
/// 这里的「昨天/今天」只是**显示用标签**，让"隔了一夜"变成看得见的字。
/// </summary>
public static class ChatTime
{
    /// <summary>
    /// 时段名。上午就是上午，深夜就是深夜，不留给模型猜。
    /// 划分按作息直觉：23 点之后算深夜，5 点之后才算清晨。
    /// </summary>
    public static string Band(DateTime t) => t.Hour switch
    {
        >= 0 and <= 4 => "深夜",
        >= 5 and <= 8 => "清晨",
        >= 9 and <= 11 => "上午",
        >= 12 and <= 13 => "中午",
        >= 14 and <= 17 => "下午",
        >= 18 and <= 19 => "傍晚",
        >= 20 and <= 22 => "晚上",
        _ => "深夜"
    };

    /// <summary>
    /// 一条历史的日期标签。今天的写「今天」，昨天的写「昨天」，
    /// 更早的写出具体月日——跨夜这件事必须变成字面上的字。
    /// </summary>
    public static string DayLabel(DateTime at, DateTime now)
    {
        if (at.Date == now.Date) return $"今天 {at:HH:mm}";
        if (at.Date == now.Date.AddDays(-1)) return $"昨天 {at:HH:mm}";
        return $"{at:MM-dd HH:mm}";
    }

    private static readonly string[] WeekdayNames =
        { "星期日", "星期一", "星期二", "星期三", "星期四", "星期五", "星期六" };

    /// <summary>
    /// 聊天记录里按天分组的那一条**居中日期条**（微信那种）。
    ///
    /// 【为什么和 DayLabel 分开】
    /// DayLabel 是喂给模型的，要带钟点（「今天 18:03」），因为模型得知道
    /// 那句话几点说的。日期条是给人看的，人已经在每条气泡下面看到 HH:mm 了，
    /// 再写一遍钟点是噪音。两者共用「今天/昨天」这套判断，但呈现不同。
    ///
    /// 【为什么星期几只管一周内】
    /// 再往前说「星期三」会让人分不清是哪个星期三，直接写月日更准。
    /// 跨年必须带年，否则去年年底和今年年初会长得一模一样。
    /// </summary>
    public static string DayDivider(DateTime at, DateTime now)
    {
        var day = at.Date;
        var today = now.Date;
        if (day == today) return "今天";
        if (day == today.AddDays(-1)) return "昨天";
        var diff = (today - day).Days;
        if (diff is >= 2 and <= 6) return WeekdayNames[(int)at.DayOfWeek];
        if (at.Year == now.Year) return $"{at.Month}月{at.Day}日";
        return $"{at.Year}年{at.Month}月{at.Day}日";
    }

    /// <summary>
    /// 这条消息属于本地的哪一天。判断日期条要不要换，只看这个，
    /// 不直接拿 DateTimeOffset 的 Date 比——库里存的是 UTC，
    /// 北京时间 23:49 在 UTC 里是当天 15:49，照 UTC 切会把跨夜的两条算成同一天。
    /// </summary>
    public static DateOnly LocalDay(DateTimeOffset at) =>
        DateOnly.FromDateTime(at.ToLocalTime().DateTime);

    /// <summary>
    /// 给一条历史消息加时间标注。标注是**系统加的**，不是用户说的原话，
    /// 系统提示里会专门说明这一点，免得模型把它当对话内容复读出来。
    /// 时间缺失（理论上不该有）时不加标注，绝不让兜底逻辑自己崩掉。
    /// </summary>
    public static string Mark(string content, DateTimeOffset? at, DateTime now)
    {
        if (at is null) return content;
        var local = at.Value.ToLocalTime().DateTime;
        return $"【{DayLabel(local, now)} · {Band(local)}】{content}";
    }

    /// <summary>距现在多久。硬数字，代码算。</summary>
    public static string Gap(DateTime from, DateTime now)
    {
        var span = now - from;
        if (span < TimeSpan.Zero) span = TimeSpan.Zero;
        if (span.TotalMinutes < 2) return "刚刚";
        if (span.TotalHours < 1) return $"{(int)span.TotalMinutes} 分钟前";
        if (span.TotalHours < 24)
        {
            var h = (int)span.TotalHours;
            return span.Minutes > 0 ? $"{h} 小时 {span.Minutes} 分钟前" : $"{h} 小时前";
        }
        return $"{(int)span.TotalDays} 天前";
    }

    /// <summary>跨了一天的间隔，把「隔了一夜」显式说出来。</summary>
    public static string GapWithNight(DateTime from, DateTime now)
    {
        var span = now - from;
        var gap = Gap(from, now);
        return from.Date != now.Date && span.TotalHours >= 4
            ? $"已经隔了一夜（{gap}）"
            : gap;
    }

    /// <summary>
    /// 系统提示里的「时间坐标」段。
    ///
    /// 【为什么要单独成段、而不是塞进已有的【关于时间】】
    /// 三条规矩是这次事故的直接解药，得让它们成块出现、措辞明确：
    /// 现在是什么时段、上次说话距今多久、旧状态默认已结束、说话要合时段。
    /// 尤其「旧状态会过期」——「去吃饭了 / 去睡了」这类只描述当时状态的话，
    /// 不说清楚，模型就会顺着最后一句的语气一直演下去。
    /// </summary>
    public static string Coordinate(DateTime now, DateTimeOffset? lastSpokeAt)
    {
        var lines = new List<string>
        {
            $"【现在的时间坐标】{now:yyyy-MM-dd}（{now:dddd}）{now:HH:mm}，{Band(now)}。这行是系统算好的，不是猜的。"
        };

        if (lastSpokeAt is { } t)
        {
            var local = t.ToLocalTime().DateTime;
            lines.Add($"在这次开口之前，你们上一句话是 {DayLabel(local, now)} 说的，{GapWithNight(local, now)}。");
        }

        lines.Add("历史每条开头的【…】是系统加的时间标注，不是他说的原话；照它判断，别自己再推一遍时间。");
        lines.Add("他讲过「去吃饭了」「去睡了」「出门了」「等会儿回来」这类只描述当时状态的话，过了这么久就默认已经结束；不要接着那个状态继续演，更别把上午当成深夜。");
        lines.Add("说话要合现在的时段：上午别道晚安、别催人睡觉；深夜别喊人起床。拿不准就按上面那个钟点说，别凭历史最后一句的语气往下续。");

        return string.Join("\n", lines);
    }
}
