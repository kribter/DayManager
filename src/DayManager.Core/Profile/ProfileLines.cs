using DayManager.Core.Planning;

namespace DayManager.Core.Profile;

/// <summary>
/// 把画像翻译成给 AI 看的几行话。
///
/// 【为什么要单独一层，而不是让提示词自己去拼】
/// 同样的画像要喂给两条链路（主动开口、对话页），
/// 各拼一遍就会出现"一边说两周、一边说十四天"这种不一致。
/// 放在 Core 里还顺带解决一件事：将来 Android 上没有本机后端，
/// 这段逻辑照样能用。
///
/// 【为什么每条都要带样本数】
/// "他启动要 20 分钟"和"看了 12 次，他启动要 20 分钟"是两种可信度。
/// 带上样本数，AI 自己就知道这话能说多重；
/// 而且它想吹成"你总是…"的时候，那个数字会拦它一下。
///
/// 【为什么最多五条】
/// 提示词越长，模型越抓不住重点。这五条是"能直接改变它建议"的那些，
/// 不是"关于他的一切"。
/// </summary>
public static class ProfileLines
{
    public const int MaxLines = 5;

    /// <summary>
    /// 把画像写成几行话。画像没攒够时返回空列表——
    /// 空着比塞一句"目前还没有足够了解"强，后者会诱导 AI 去说客套话。
    /// </summary>
    public static IReadOnlyList<string> Describe(UserProfile profile)
    {
        var lines = new List<string>();

        if (profile.StartDelay is { } delay)
        {
            lines.Add(
                $"他启动一件事平均要 {DurationText.About(delay.MedianMinutes)}"
                + $"（看了 {delay.SampleCount} 次，中位数 {delay.MedianMinutes} 分钟，平均 {delay.AverageMinutes} 分钟）"
                + "——所以别等他\"准备好了\"再开始。");
        }

        if (profile.Estimation is { } est)
        {
            // 比值大于 1 说明他低估了自己要做多久，排程时得往少了排。
            var over = (int)Math.Round((est.ActualOverEstimate - 1) * 100);
            var how = over >= 10
                ? $"他估的时间通常比实际少 {over}%（看了 {est.SampleCount} 次）——排的时候要往少了排"
                : over <= -10
                    ? $"他估的时间通常比实际多 {-over}%（看了 {est.SampleCount} 次）——他偏保守，可以稍微多给一点"
                    : $"他估得挺准（看了 {est.SampleCount} 次，偏差在 {Math.Abs(over)}% 以内）";

            lines.Add(how);
        }

        if (profile.ActiveHours.Count > 0)
        {
            var top = profile.ActiveHours
                .Take(2)
                .Select(x => $"{x.Hour} 点")
                .ToList();

            lines.Add($"他在 {string.Join("、", top)} 前后动得最多，安排要紧的事优先放这几段。");
        }

        if (profile.SkipReasons.Count > 0)
        {
            var reasons = string.Join("、",
                profile.SkipReasons.Select(x => $"「{x.Reason}」{x.Count} 次"));

            lines.Add($"他没做成的原因里：{reasons}。");
        }

        if (profile.RecentDays.Count >= 3)
        {
            var planned = profile.RecentDays.Sum(x => x.Planned);
            var completed = profile.RecentDays.Sum(x => x.Completed);

            if (planned > 0)
            {
                lines.Add(
                    $"最近 {profile.RecentDays.Count} 天，安排了 {planned} 件、做了 {completed} 件。");
            }
        }

        return lines.Take(MaxLines).ToList();
    }

    /// <summary>
    /// 把一条趋势写成给他自己看的话。
    ///
    /// 【为什么给 AI 和给他自己看的是两种写法】
    /// 给 AI 的那份要精确、要带样本数，好让它判断轻重；
    /// 给他自己看的那份只说方向，不评价好坏——
    /// "你启动慢了"是在陈述，"你这周变差了"是在打分。
    /// 这个软件是帮他启动的，不是给他打分的。
    ///
    /// 【为什么数据不够时写"还在攒"，而不是不显示】
    /// 界面上什么都不显示，他会以为这个功能坏了。
    /// 说清楚"还差几天"，反而让人愿意多用几天再回来看。
    /// </summary>
    public static string DescribeForUser(TrendItem item)
    {
        if (!item.EnoughData)
        {
            return "还在攒数据（两个周期各要有几天有安排）";
        }

        if (item.BeforeText == item.AfterText)
        {
            return $"{item.AfterText}（和前一个周期一样）";
        }

        return $"{item.BeforeText} → {item.AfterText}，{item.ChangeWord}";
    }

    /// <summary>
    /// 画像里的一条，给他自己看。
    ///
    /// 【为什么 Remaining 和 Text 放在一起】
    /// 攒够的那条要显示结论，没攒够的那条要显示"还差几次"。
    /// 两者是同一件事的两个阶段，用两个类型表达会让调用方多写一遍分支。
    /// </summary>
    /// <param name="Remaining">还差几个样本才有结论；0 表示这条已经是结论。</param>
    public sealed record SelfFact(string Label, string Text, int Remaining)
    {
        public bool IsPending => Remaining > 0;
    }

    /// <summary>
    /// 把画像写成给他自己看的几条。
    ///
    /// 【为什么要做这一版：画像以前只喂给 AI，他自己看不到】
    /// 算出来的东西不给他看，等于这个功能只对 AI 有效——
    /// 而他用这个软件的目的，是"看见自己真实的样子"，
    /// 这件事本身就有用，不必等它变成一句建议。
    ///
    /// 【为什么没攒够的也说，而不是干脆不显示】
    /// 一个永远空着的区块等于不存在。说"再记 4 次就有"，他才知道
    /// 这些数字是从哪儿长出来的，也才有理由继续用。
    ///
    /// 【为什么跳过的原因不进这一版】
    /// "你没做成的时候多半是「太累」"——这条对 AI 有用（它据此换建议），
    /// 对他就是一张失败清单。这一页的规矩是陈述事实，不是复盘他的失败。
    /// </summary>
    public static IReadOnlyList<SelfFact> DescribeForSelf(UserProfile profile)
    {
        var facts = new List<SelfFact>();

        // ① 什么时候在状态。这条几乎是最快能攒出来的——只要用够两小时。
        if (profile.ActiveHours.Count > 0)
        {
            var top = profile.ActiveHours
                .Take(2)
                .Select(x => $"{x.Hour} 点")
                .ToList();

            facts.Add(new SelfFact(
                "什么时候在状态",
                $"{string.Join("、", top)}前后动得最多",
                0));
        }

        // ② 有记录的这些天的量。只给数，不给完成率——
        // 完成率会变成一个分数，而这一页不打分。
        if (profile.RecentDays.Count > 0)
        {
            var planned = profile.RecentDays.Sum(x => x.Planned);
            var completed = profile.RecentDays.Sum(x => x.Completed);

            if (planned > 0)
            {
                facts.Add(new SelfFact(
                    // 不写"有记录的 N 天"：N 是个会变的数，而这一行要回答的是
                    // "最近这两周大概什么节奏"。范围固定，数字才不会每天变样。
                    "最近两周",
                    $"安排了 {planned} 件、做了 {completed} 件",
                    0));
            }
        }

        // ③ 启动延迟：从"计划几点"到"真的动手"。
        if (profile.StartDelay is { } delay)
        {
            facts.Add(new SelfFact(
                "从计划到动手",
                $"通常要 {DurationText.About(delay.MedianMinutes)}（看了 {delay.SampleCount} 次）",
                0));
        }
        else
        {
            facts.Add(new SelfFact(
                "从计划到动手",
                $"再记 {Short(ProfileCalculator.MinSample - profile.StartDelaySamples)}"
                + " 次「按下启动」，就能看出你一般拖多久",
                Short(ProfileCalculator.MinSample - profile.StartDelaySamples)));
        }

        // ④ 估得准不准。
        if (profile.Estimation is { } est)
        {
            // 比值大于 1 说明他低估了自己要做多久。
            var over = (int)Math.Round((est.ActualOverEstimate - 1) * 100);

            var text = over >= 10
                ? $"你估的时间通常不够用，实际多花 {over}%"
                : over <= -10
                    ? $"你估的时间通常用不完，实际少花 {-over}%"
                    : $"估得挺准，差不到 {Math.Max(over, 10)}%";

            facts.Add(new SelfFact("估时准不准", text, 0));
        }
        else
        {
            facts.Add(new SelfFact(
                "估时准不准",
                $"再记 {Short(ProfileCalculator.MinSample - profile.EstimationSamples)}"
                + " 次「估时 + 实际用时」，就能看出你估得准不准",
                Short(ProfileCalculator.MinSample - profile.EstimationSamples)));
        }

        return facts;
    }

    /// <summary>
    /// 还差几次。样本数算出负数时（门槛被调小过、或阈值不一致）按 1 说，
    /// 免得界面上出现"再记 -2 次"这种话。
    /// </summary>
    private static int Short(int remaining) => Math.Max(remaining, 1);

    /// <summary>
    /// 一个周期的事实，写成一句给他自己看的话。
    ///
    /// 【为什么需要它：前后两个周期凑不齐时，趋势那块本来是空的】
    /// 那个对比要两个周期各有三天以上有安排，而前一个周期在刚开始用时
    /// 一天都没有——于是页面上一整块只有"还在攒"，要空等一周。
    /// 攒不够对比的时候，至少把"这一周到底怎么样"说出来。
    /// </summary>
    public static string DescribeRecentWeek(PeriodStat week)
    {
        if (week.Planned == 0 && week.ActiveMinutes == 0)
        {
            return "最近七天还没有可算的记录";
        }

        // 时长是 0 的时候不提"做事 未估时"——那是给排程用的说法，放这儿像报错。
        var activePart = week.ActiveMinutes > 0
            ? $"，做事时间 {DurationText.About(week.ActiveMinutes)}"
            : string.Empty;

        return week.Planned == 0
            ? $"这周没排安排{activePart}"
            : $"安排了 {week.Planned} 件、做了 {week.Completed} 件{activePart}";
    }

    /// <summary>
    /// 把画像拼成提示词里的一整段。主动开口和对话页共用这一份，
    /// 两边才会用同一副口径说他。
    /// </summary>
    ///
    /// 【为什么要明写"不要念给他听"】
    /// 画像是最容易被滥用的一类信息：它看起来很懂他，
    /// 所以模型会忍不住说出来——"根据记录，你平均要 20 分钟才能启动"。
    /// 这种话一出口，关心就变成了监控，而且他会立刻关掉这个功能。
    /// 这些数字唯一的用途是让他接到的建议更准，不是让他知道自己被怎么描述。
    ///
    /// 【为什么样本不足时整段不出现】
    /// 与其给一句"目前还不太了解你"，不如什么都不给。
    /// 给了它会去接话，而它一接话就容易编。
    public static string ToPrompt(UserProfile profile)
    {
        var lines = Describe(profile);

        if (lines.Count == 0)
        {
            return string.Empty;
        }

        var sb = new System.Text.StringBuilder();

        sb.AppendLine("【你了解他的地方——只用来调整你给的建议，不要念给他听】");

        foreach (var line in lines)
        {
            sb.AppendLine("- " + line);
        }

        sb.AppendLine("括号里是样本数。样本少的时候别当成定论，更不许说「你总是」——");
        sb.AppendLine("那是拿三次行为给人贴标签，说错了比不说更伤人。");
        sb.AppendLine("这几条只用来让你给的建议更准，不要复述给他。");
        sb.AppendLine();

        return sb.ToString();
    }
}
