namespace DayManager.Core.Planning;

/// <summary>
/// 一段时间：从 Start（含）到 End（不含）。
/// </summary>
public readonly record struct TimeInterval(DateTimeOffset Start, DateTimeOffset End)
{
    /// <summary>长度为零或倒挂的区间。它们不参与合并，也不该混进统计里。</summary>
    public bool IsEmpty => End <= Start;

    public int Seconds => (int)(End - Start).TotalSeconds;
}

/// <summary>
/// 把很多段时间合在一起，重叠的部分只算一次。
///
/// 【为什么需要它：同一个小时不该被数两遍】
/// 电脑使用记录和任务计时是两路独立的数据，
/// 而你在电脑上做一件事时，两路都会记下同一个钟头——
/// 一路来自"前台在用什么"，一路来自"你按了启动"。
/// 直接相加的话，那段时间会被算成两倍，分母虚高，
/// 娱乐占比就低得离谱，AI 反而不开口了。
///
/// 【为什么放在 Core 里，而不是写在统计的那一处】
/// 这是纯函数：给它一组区间，它还一组区间，不碰数据库、不看时钟。
/// 这种东西最好写、也最容易写错（边界重叠、相邻、倒挂），
/// 单独放一处才能被冒烟测试直接覆盖。
/// </summary>
public static class IntervalUnion
{
    /// <summary>
    /// 合并重叠与紧邻的区间，按开始时间升序返回。
    ///
    /// 【为什么"紧邻"也要合并】
    /// 两段首尾相接（前一段 10:00-11:00，后一段 11:00-12:00）本来就是连着的一段时间，
    /// 分成两段和合成一段在秒数上没差别，但后面按天切分时，
    /// 分成两段会在切分点来回转，多写一层判断不如直接合成一段。
    /// </summary>
    public static List<TimeInterval> Merge(IEnumerable<TimeInterval> intervals)
    {
        var ordered = intervals
            .Where(x => !x.IsEmpty)
            .OrderBy(x => x.Start)
            .ToList();

        var merged = new List<TimeInterval>();

        foreach (var current in ordered)
        {
            if (merged.Count == 0 || current.Start > merged[^1].End)
            {
                merged.Add(current);
                continue;
            }

            var last = merged[^1];
            if (current.End > last.End)
            {
                merged[^1] = last with { End = current.End };
            }
        }

        return merged;
    }

    /// <summary>
    /// 合并之后一共多少秒（重叠只算一次）。
    /// </summary>
    public static int TotalSeconds(IEnumerable<TimeInterval> intervals)
    {
        return Merge(intervals).Sum(x => x.Seconds);
    }

    /// <summary>
    /// 合并之后按天摊开：一天里的零点到二十四点各占了多少秒。
    ///
    /// 【为什么必须自己切，不能按"区间属于哪天"来分】
    /// 一段区间会横跨午夜（晚上 23:40 做到凌晨 0:20），
    /// 它不属于任何一天，只能按落在每一天里的比例切开。
    ///
    /// 【为什么按本地的"天"分，不是按 UTC 的"天"】
    /// 库里的时间存成 UTC 毫秒，读回来的偏移量是 0。
    /// 直接按 UTC 分的话，晚上八点以后的时间会被算进第二天——
    /// 而这张表正是要回答"今天做了多少"的，错位就全错了。
    /// </summary>
    public static Dictionary<DateOnly, int> SecondsByDay(IEnumerable<TimeInterval> intervals)
    {
        var result = new Dictionary<DateOnly, int>();

        foreach (var interval in Merge(intervals))
        {
            var cursor = interval.Start.ToLocalTime();
            var stop = interval.End.ToLocalTime();

            while (cursor < stop)
            {
                var day = DateOnly.FromDateTime(cursor.DateTime);

                // 这一天的终点 = 明天零点。用 cursor 的偏移量构造，
                // 免得跨夏令时的地方（出差时会遇到）算出来差一小时。
                var dayEnd = new DateTimeOffset(
                    day.ToDateTime(TimeOnly.MinValue).AddDays(1), cursor.Offset);

                var sliceEnd = stop < dayEnd ? stop : dayEnd;

                var seconds = (int)(sliceEnd - cursor).TotalSeconds;
                result[day] = result.TryGetValue(day, out var has) ? has + seconds : seconds;

                cursor = sliceEnd;
            }
        }

        return result;
    }

    /// <summary>
    /// 把区间裁到 [from, to) 这个窗口里，窗口外的部分丢掉。
    ///
    /// 【为什么要在合并之前裁】
    /// 查一年的数据时，一条从前年开始的超长区间会一直延伸进窗口，
    /// 先裁再合并，合并出来的每一段都落在窗口内，后面按天切分才不会跑出边界。
    /// </summary>
    public static TimeInterval? Clip(TimeInterval interval, DateTimeOffset from, DateTimeOffset to)
    {
        var start = interval.Start < from ? from : interval.Start;
        var end = interval.End > to ? to : interval.End;

        return end <= start ? null : new TimeInterval(start, end);
    }
}
