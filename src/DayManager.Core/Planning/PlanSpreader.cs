namespace DayManager.Core.Planning;

/// <summary>
/// 把一串子任务摊到截止日之前的每一天。
///
/// 【为什么日期不让 AI 直接给】
/// 让模型算日期是件很危险的事：它会算错天数、搞混周末，
/// 而且算错了不会报错，只会静默地把任务排到一个不该出现的日子上。
/// 日期是确定性的算术，交给代码算，一次算对；
/// AI 只负责它真正擅长的事——拆得合不合理、顺序对不对、估时准不准。
/// 这是"能确定的部分绝不让模型碰"的原则。
///
/// 【为什么这个类放在 Core 里】
/// 它是纯计算，不碰数据库、不碰界面、不依赖任何框架。
/// 放在 Core 意味着两端（手机、电脑）和将来服务端都用的是同一份算法，
/// 不会出现"手机上算出来 8 天、电脑上算出来 9 天"这种事。
/// 附带好处是它可以在控制台里直接跑测试，不用启动界面。
/// </summary>
public static class PlanSpreader
{
    /// <summary>
    /// 一件事每天最多能占多少分钟（默认值）。
    ///
    /// 【这个额度属于"这一件事"，不是一天的总余量】
    /// 最早的注释是按"扣掉正事、吃饭、通勤之后还剩多少"推出来的——
    /// 那算的是**全天可用余量**，口径是错的。照那个理解，
    /// 当天已经有别的事时，新拆的这件事就该被压到几乎排不进去；
    /// 而用户要的是"这件事每天最多给它两小时，别的是别的事"。
    /// 两件事各占两小时，不等于一天只有两小时。
    /// 推演过程不改，代码迟早又被照着错口径改回那一版，所以注释要跟着口径一起改。
    ///
    /// 【为什么默认 120，而且偏向保守】
    /// 需求文档里写得明白：排程的保守程度是这个功能的生死线，
    /// 拆解的智能程度只是加分项。因为一旦排乐观了，AI 就成了
    /// 一个比用户更会高估、还带着权威感的计划者，失败时的挫败感会更重。
    /// 120 是用户认可的"给一件大事的每日上限"，也是个稳妥的冷启动起点。
    /// 宁可第一天轻松完成、后面再加，也不要第一天就排到做不完。
    /// 这个值必须用户可调——不同的人、不同的事差别很大。
    /// </summary>
    public const int DefaultDailyMinutes = 120;

    /// <summary>
    /// 摊分。
    /// </summary>
    /// <param name="tasks">按顺序排好的子任务（顺序由 AI 决定，通常是先难后易）。</param>
    /// <param name="startDate">从哪天开始做（一般是明天，今天通常已经被别的事占满了）。</param>
    /// <param name="deadline">最晚必须做完的那天。</param>
    /// <param name="dailyMinutes">这件事每天最多能占多少分钟（不是一天的总余量）。</param>
    public static SpreadingResult Spread(
        IReadOnlyList<PlannedTask> tasks,
        DateOnly startDate,
        DateOnly deadline,
        int dailyMinutes = DefaultDailyMinutes)
    {
        // 保护：每天额度至少要大于 0，否则下面的除法会炸。
        if (dailyMinutes <= 0)
        {
            dailyMinutes = DefaultDailyMinutes;
        }

        var availableDays = (deadline.DayNumber - startDate.DayNumber) + 1;
        if (availableDays < 1)
        {
            // 截止日比开始日还早（比如用户把截止日填成了昨天）。
            // 这时不抛异常，而是如实报告"一天都排不下"，
            // 让调用方去提示用户改日期——抛异常只会让界面闪退。
            availableDays = 0;
        }

        var scheduled = new List<ScheduledTask>();
        var overflow = new List<PlannedTask>();

        // 每一天的实际装载量。界面要靠它把"某天超出预算"标出来，
        // 否则用户只能自己把那一列加起来才知道超没超。
        var dayLoads = new Dictionary<DateOnly, int>();

        var currentDay = startDate;
        var remainingToday = dailyMinutes;
        var daysUsed = 0;
        var totalMinutes = 0;

        foreach (var task in tasks)
        {
            // 估时缺失或明显不合理时兜底成一个小时的块。
            // 【为什么要兜底】AI 偶尔会给出 0 或者一个荒谬的大数，
            // 直接拿去算会让整个排期失去意义。兜底之后至少结果是能用的。
            var minutes = task.EstimateMinutes is > 0 and <= 24 * 60
                ? task.EstimateMinutes
                : 60;

            totalMinutes += minutes;

            // 【换天的条件为什么是"放不下 且 今天已经排了东西"】
            // 只判断"放不下"就换天的话，第一条任务只要超过一天的额度，
            // 第一天就会被白白跳过——测试就是在这个点上抓到它的。
            // 加一个 remainingToday < dailyMinutes 的判断，意思是"今天已经占用了"：
            // 今天还是空的，就算这一条超过一天的额度也直接放进去，
            // 因为一条任务超过一天的量，说明是拆得不够细，
            // 该回去重拆，而不是在这里把它切碎到两天——
            // 跨天做的任务要求用户记住"昨天做到哪了"，那本身就是启动摩擦。
            if (minutes > remainingToday && remainingToday < dailyMinutes)
            {
                currentDay = currentDay.AddDays(1);
                daysUsed++;
                remainingToday = dailyMinutes;
            }

            if (daysUsed >= availableDays)
            {
                // 天数用完了，剩下的都排不进去。
                // 用 with 把兜底修正过的估时写回去，
                // 这样外面统计溢出总量时拿到的是修正后的值，和实际排期口径一致。
                overflow.Add(task with { EstimateMinutes = minutes });
                continue;
            }

            scheduled.Add(new ScheduledTask(task, currentDay));
            remainingToday -= minutes;

            // 记下这一天累计装了多少。
            // 【为什么单独记一份，而不是事后从 scheduled 里 GroupBy 出来】
            // 两处口径必须一致：这里记的是"放进去的时候算的账"，
            // 重算一遍有可能因为边界判断差一点而对不上，
            // 而"界面显示的合计"和"判定超没超的依据"不一致是最难解释的那种 bug。
            dayLoads[currentDay] = dayLoads.GetValueOrDefault(currentDay) + minutes;
        }

        // 已排上的天数 + 溢出部分按每天额度估算还需要的天数。
        // 分开算是因为溢出部分并没有真的落到某一天上，只能估算；
        // 直接用"最后一条的日期"减出来会把这部分漏掉。
        var scheduledDays = scheduled.Count == 0
            ? 0
            : (scheduled[^1].Date.DayNumber - startDate.DayNumber) + 1;

        var overflowDays = overflow.Count == 0
            ? 0
            : (int)Math.Ceiling(overflow.Sum(x => x.EstimateMinutes) / (double)dailyMinutes);

        return new SpreadingResult
        {
            Scheduled = scheduled,
            Overflow = overflow,
            RequiredDays = scheduledDays + overflowDays,
            AvailableDays = availableDays,
            TotalMinutes = totalMinutes,
            DailyMinutes = dailyMinutes,
            DayLoads = dayLoads
        };
    }
}
