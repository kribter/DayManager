using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DayManager.Data.Repositories;

namespace DayManager.App.ViewModels;

/// <summary>时间页看的是哪一段。</summary>
public enum UsageSpan
{
    Day,
    Week,
    Month,
    Year
}

/// <summary>
/// 时间页的大脑：使用统计。
///
/// 【这一页要回答的问题，以及刻意没做的事】
/// 要回答的是"我的时间到底去哪了"，顺带让人看到自己几点最在状态。
/// 刻意没做排行榜式的比较、没有"你比昨天差"这类文案——
/// 这个软件的用户是来对抗逃避的，不是来被一个统计页面再骂一遍的。
/// 所以：数字照实给，评价不给。
///
/// 【为什么四个跨度共用一套加载逻辑】
/// 只是区间长短不同。各写一套的话，切换跨度时数字对不上（日视图 3 小时、
/// 周视图里那天 2.5 小时），而没人能看出是哪一套错了。见 UsageRepository.GetRangeAsync。
/// </summary>
public partial class TimeViewModel : ObservableObject
{
    private readonly UsageRepository _usage;

    /// <summary>
    /// 自动刷新间隔。
    ///
    /// 【为什么是 20 秒，而不是跟着采集的 5 秒】
    /// 采集每 5 秒写一次库，但刷新一次要重新聚合一整天的数据、再按小时摊一遍。
    /// 5 秒刷一次人眼也看不出差别，只是白白多跑几倍查询。
    /// 20 秒是"切完应用回头就能看见数字动了"和"别让电脑一直算"之间的平衡。
    /// </summary>
    private static readonly TimeSpan AutoRefreshInterval = TimeSpan.FromSeconds(20);

    /// <summary>同一时刻只允许一次刷新在跑（翻页点快了、自动刷新撞上手动刷新都会撞车）。</summary>
    private readonly SemaphoreSlim _refreshGate = new(1, 1);

    private readonly CancellationTokenSource _autoRefreshCts = new();
    private bool _autoRefreshStarted;

    /// <summary>当前看的是哪一天。周/月/年都从它推出来。</summary>
    private DateOnly _anchor = DateOnly.FromDateTime(DateTime.Today);

    public TimeViewModel(UsageRepository usage)
    {
        _usage = usage;
    }

    /// <summary>
    /// 开始自动刷新（幂等，重复调用没有副作用）。
    ///
    /// 【为什么必须自己定时，不能只靠 OnAppearing】
    /// Shell 切 Tab 时**并不保证**触发 `OnAppearing`（M6 就栽过一次：
    /// 改完数据切回列表页还是旧的）。而采集是后台一直在跑的——
    /// 人停在时间页上什么都不点，数字也该自己长起来。
    /// 靠"什么时候会变"的约定去猜，不如定时重查：
    /// 一个人用的数据量，多查一次不值得省。
    /// </summary>
    public void StartAutoRefresh()
    {
        if (_autoRefreshStarted)
        {
            return;
        }

        _autoRefreshStarted = true;

        _ = Task.Run(async () =>
        {
            while (!_autoRefreshCts.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(AutoRefreshInterval, _autoRefreshCts.Token);
                }
                catch (TaskCanceledException)
                {
                    return;
                }

                await RefreshAsync(quiet: true);
            }
        });
    }

    /// <summary>停止自动刷新。目前只有程序退出才需要（页面和程序一样长命）。</summary>
    public void StopAutoRefresh() => _autoRefreshCts.Cancel();

    /// <summary>
    /// 刷新一次。`quiet` 表示"后台自动刷的"，出错也不弹框——
    /// 每 20 秒弹一个错误框，比数字暂时停留在旧值烦人得多。
    /// </summary>
    private async Task RefreshAsync(bool quiet)
    {
        // 已经有刷新在跑就直接跳过：这次错过没关系，20 秒后还有一轮。
        if (!await _refreshGate.WaitAsync(0))
        {
            return;
        }

        try
        {
            await LoadCoreAsync(quiet);
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    public ObservableCollection<AppUsageRow> Apps { get; } = new();

    [ObservableProperty]
    public partial UsageSpan Span { get; set; } = UsageSpan.Day;

    [ObservableProperty]
    public partial string RangeTitle { get; set; } = string.Empty;

    /// <summary>总时长，例如「3 小时 12 分」。</summary>
    [ObservableProperty]
    public partial string TotalText { get; set; } = "0 分钟";

    /// <summary>副标题：记录了几段、从几点到几点。</summary>
    [ObservableProperty]
    public partial string NoteText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string EntertainmentText { get; set; } = string.Empty;

    /// <summary>
    /// 娱乐时长是否展开。
    ///
    /// 【为什么默认收起来】
    /// 回顾界面的第一原则是正反馈优先（需求 2.9）。
    /// 一进来就把"你今天刷了两小时"摆在最显眼的位置，
    /// 结果是人不敢打开这一页——而不看的统计等于没做。
    /// 想看的人点一下就在，不用藏得更深。
    /// </summary>
    [ObservableProperty]
    public partial bool IsEntertainmentShown { get; set; }

    /// <summary>折叠按钮上的字。展开后才看得见具体数字。</summary>
    public string EntertainmentToggleText => IsEntertainmentShown ? "收起" : "看看娱乐时长";

    partial void OnIsEntertainmentShownChanged(bool value) =>
        OnPropertyChanged(nameof(EntertainmentToggleText));

    [ObservableProperty]
    public partial bool IsEmpty { get; set; } = true;

    [ObservableProperty]
    public partial bool HasApps { get; set; }

    [ObservableProperty]
    public partial bool CanGoNext { get; set; }

    /// <summary>柱状图的说明文字（"按小时"/"按天"/"按月"）。</summary>
    [ObservableProperty]
    public partial string ChartCaption { get; set; } = string.Empty;

    /// <summary>柱子数据。界面拿到它之后要重画，见 TimePage.xaml.cs。</summary>
    [ObservableProperty]
    public partial IReadOnlyList<BarItem> Bars { get; set; } = Array.Empty<BarItem>();

    /// <summary>四个跨度按钮的高亮状态。XAML 用 DataTrigger 绑它们。</summary>
    public bool IsDay => Span == UsageSpan.Day;
    public bool IsWeek => Span == UsageSpan.Week;
    public bool IsMonth => Span == UsageSpan.Month;
    public bool IsYear => Span == UsageSpan.Year;

    [RelayCommand]
    private void SetSpan(string span)
    {
        if (!Enum.TryParse<UsageSpan>(span, out var parsed) || parsed == Span)
        {
            return;
        }

        Span = parsed;

        // 切换跨度时不改"看的是哪一天"：从"10月27日"切到"周"应该落在本周，
        // 而不是跳回今天。这个行为符合"我现在在看 27 号"的心理模型。
        _ = RefreshAsync(quiet: false);
    }

    [RelayCommand]
    private async Task PrevAsync()
    {
        _anchor = Span switch
        {
            UsageSpan.Day => _anchor.AddDays(-1),
            UsageSpan.Week => _anchor.AddDays(-7),
            UsageSpan.Month => _anchor.AddMonths(-1),
            _ => _anchor.AddYears(-1)
        };

        await RefreshAsync(quiet: false);
    }

    [RelayCommand]
    private async Task NextAsync()
    {
        if (!CanGoNext)
        {
            return;
        }

        _anchor = Span switch
        {
            UsageSpan.Day => _anchor.AddDays(1),
            UsageSpan.Week => _anchor.AddDays(7),
            UsageSpan.Month => _anchor.AddMonths(1),
            _ => _anchor.AddYears(1)
        };

        await RefreshAsync(quiet: false);
    }

    [RelayCommand]
    private void ToggleEntertainment() => IsEntertainmentShown = !IsEntertainmentShown;

    [RelayCommand]
    private async Task LoadAsync() => await RefreshAsync(quiet: false);

    /// <summary>真正干活的那一段。手动刷新走 `LoadAsync`，自动刷新直接调这里。</summary>
    private async Task LoadCoreAsync(bool quiet)
    {
        try
        {
            var (from, to) = Range();
            RangeTitle = BuildTitle(from, to);
            CanGoNext = CanMoveNext();

            // 四个跨度按钮的高亮要跟着变。
            OnPropertyChanged(nameof(IsDay));
            OnPropertyChanged(nameof(IsWeek));
            OnPropertyChanged(nameof(IsMonth));
            OnPropertyChanged(nameof(IsYear));

            var range = await _usage.GetRangeAsync(from, to);

            TotalText = FormatDuration(range.TotalSeconds);
            IsEmpty = range.TotalSeconds == 0;

            NoteText = range.SessionCount == 0
                ? string.Empty
                : $"记录 {range.SessionCount} 段 · {range.FirstAt:HH:mm} 开始 · {range.LastAt:HH:mm} 结束";

            EntertainmentText = range.TotalSeconds == 0
                ? string.Empty
                : $"娱乐 {FormatDuration(range.EntertainmentSeconds)}"
                  + $"（占 {range.EntertainmentSeconds * 100 / range.TotalSeconds}%）";

            Apps.Clear();
            foreach (var row in BuildAppRows(range))
            {
                Apps.Add(row);
            }

            HasApps = Apps.Count > 0;

            Bars = Span == UsageSpan.Day
                ? await BuildHourlyBarsAsync(from)
                : BuildDayBars(range, from, to);

            ChartCaption = Span switch
            {
                UsageSpan.Day => "按小时",
                UsageSpan.Week => "按天",
                UsageSpan.Month => "按天",
                _ => "按月"
            };
        }
        catch (Exception ex)
        {
            if (!quiet)
            {
                await ReportErrorAsync(ex);
            }

            // 自动刷新失败了就等下一轮。数字暂时停在旧值是可以接受的，
            // 而每 20 秒弹一个框会把人赶离这一页。
        }
    }

    // ================= 区间与标题 =================

    private (DateOnly From, DateOnly To) Range() => Span switch
    {
        UsageSpan.Day => (_anchor, _anchor.AddDays(1)),
        // 周一为一周之首：中国习惯，而且"这周做了多少"的问题里，
        // 周末属于刚刚过去的那一周，不是新一周的开头。
        UsageSpan.Week => (WeekStart(_anchor), WeekStart(_anchor).AddDays(7)),
        UsageSpan.Month => (new DateOnly(_anchor.Year, _anchor.Month, 1),
                            new DateOnly(_anchor.Year, _anchor.Month, 1).AddMonths(1)),
        _ => (new DateOnly(_anchor.Year, 1, 1), new DateOnly(_anchor.Year + 1, 1, 1))
    };

    private static DateOnly WeekStart(DateOnly day)
    {
        // DayOfWeek 里周日是 0，要先挪成"周一 = 0"再回退。
        var offset = ((int)day.DayOfWeek + 6) % 7;
        return day.AddDays(-offset);
    }

    private string BuildTitle(DateOnly from, DateOnly to)
    {
        var today = DateOnly.FromDateTime(DateTime.Today);

        switch (Span)
        {
            case UsageSpan.Day:
                var name = from == today ? "今天" : WeekdayName(from.DayOfWeek);
                return $"{from.Month}月{from.Day}日 {name}";

            case UsageSpan.Week:
                var end = to.AddDays(-1);
                return $"{from.Month}月{from.Day}日 – {end.Month}月{end.Day}日";

            case UsageSpan.Month:
                return $"{from.Year}年{from.Month}月";

            default:
                return $"{from.Year}年";
        }
    }

    private static string WeekdayName(DayOfWeek day) => day switch
    {
        DayOfWeek.Monday => "周一",
        DayOfWeek.Tuesday => "周二",
        DayOfWeek.Wednesday => "周三",
        DayOfWeek.Thursday => "周四",
        DayOfWeek.Friday => "周五",
        DayOfWeek.Saturday => "周六",
        _ => "周日"
    };

    /// <summary>
    /// 能不能往后翻。
    ///
    /// 【为什么不让翻到未来】
    /// 未来的时间是 0，翻过去只有一片空白和"你今天什么都没做"的错觉。
    /// 一个统计页面不该能制造出这种画面。
    /// </summary>
    private bool CanMoveNext()
    {
        var today = DateOnly.FromDateTime(DateTime.Today);

        return Span switch
        {
            UsageSpan.Day => _anchor < today,
            UsageSpan.Week => WeekStart(_anchor).AddDays(7) <= today,
            UsageSpan.Month => new DateOnly(_anchor.Year, _anchor.Month, 1) < new DateOnly(today.Year, today.Month, 1),
            _ => _anchor.Year < today.Year
        };
    }

    // ================= 图表 =================

    private async Task<IReadOnlyList<BarItem>> BuildHourlyBarsAsync(DateOnly day)
    {
        var hourly = await _usage.GetHourlyAsync(day);

        var bars = new List<BarItem>(24);
        for (var hour = 0; hour < 24; hour++)
        {
            // 24 根柱子如果每根都标数字会糊成一片，每三小时标一个。
            var label = hour % 3 == 0 ? hour.ToString() : string.Empty;
            bars.Add(new BarItem(label, hourly[hour]));
        }

        return bars;
    }

    /// <summary>
    /// 按天/按月的柱子。区间里没有记录的那些天要补 0，
    /// 否则柱子会挤在一起，看着像"这几天连在一起用了很久"。
    /// </summary>
    private IReadOnlyList<BarItem> BuildDayBars(UsageRange range, DateOnly from, DateOnly to)
    {
        var byDay = range.Days.ToDictionary(x => x.Day, x => x.TotalSeconds);

        if (Span == UsageSpan.Year)
        {
            var months = new List<BarItem>(12);
            for (var month = 1; month <= 12; month++)
            {
                var seconds = range.Days
                    .Where(x => x.Day.Month == month)
                    .Sum(x => x.TotalSeconds);

                months.Add(new BarItem($"{month}", seconds));
            }

            return months;
        }

        var bars = new List<BarItem>();
        var isWeek = Span == UsageSpan.Week;

        for (var day = from; day < to; day = day.AddDays(1))
        {
            var seconds = byDay.TryGetValue(day, out var s) ? s : 0;

            // 周视图标周一/周三…，月视图每五天标一个日子。
            var label = isWeek
                ? (day.DayOfWeek == DayOfWeek.Monday ? "一" : string.Empty)
                : (day.Day == 1 || day.Day % 5 == 0 ? day.Day.ToString() : string.Empty);

            bars.Add(new BarItem(label, seconds));
        }

        // 周视图只标了"一"，太孤单，把最后一天补成"日"当刻度终点。
        if (isWeek && bars.Count > 0)
        {
            bars[^1] = bars[^1] with { Label = "日" };
        }

        return bars;
    }

    // ================= 应用排行 =================

    private static IEnumerable<AppUsageRow> BuildAppRows(UsageRange range)
    {
        const int TopCount = 8;

        var top = range.Apps.Take(TopCount).ToList();
        if (top.Count == 0)
        {
            yield break;
        }

        // 条形长度按"第一名"归一：这里要表达的是"谁占大头"，
        // 而不是"你用了百分之几的时间"——后者会把所有条都压得很短，看不出差别。
        var max = top[0].Seconds;

        foreach (var app in top)
        {
            yield return new AppUsageRow(
                app.DisplayName,
                FormatDuration(app.Seconds),
                max == 0 ? 0 : (double)app.Seconds / max,
                app.IsEntertainment);
        }

        if (range.Apps.Count > TopCount)
        {
            var rest = range.Apps.Skip(TopCount).ToList();
            yield return new AppUsageRow(
                $"其余 {rest.Count} 个应用",
                FormatDuration(rest.Sum(x => x.Seconds)),
                max == 0 ? 0 : (double)rest.Sum(x => x.Seconds) / max,
                false);
        }
    }

    // ================= 格式化 =================

    /// <summary>
    /// 把秒数说成人话。
    ///
    /// 【为什么不是「3.2 小时」】
    /// "3.2 小时"要心算才知道是 3 小时 12 分，而人对自己时间的记忆是"三个多小时"。
    /// 分钟级的数字看得见、对得上，也更容易和自己的感受比对。
    /// </summary>
    internal static string FormatDuration(int seconds)    {
        if (seconds <= 0)
        {
            return "0 分钟";
        }

        var totalMinutes = seconds / 60;
        if (totalMinutes < 1)
        {
            return "不到 1 分钟";
        }

        var hours = totalMinutes / 60;
        var minutes = totalMinutes % 60;

        if (hours == 0)
        {
            return $"{minutes} 分钟";
        }

        return minutes == 0 ? $"{hours} 小时" : $"{hours} 小时 {minutes} 分";
    }

    private static async Task ReportErrorAsync(Exception ex)
    {
        var page = Application.Current?.Windows.Count > 0 ? Application.Current.Windows[0].Page : null;
        if (page is not null)
        {
            await page.DisplayAlertAsync("出错了", ex.Message, "好");
        }
    }
}

/// <summary>
/// 应用排行里的一行。
///
/// 【为什么 Ratio 是按第一名归一，不是按总时长】
/// 按总时长归一的话，前八名加起来如果是总时长的 70%，
/// 所有条都会被压到很短，粗细差别看不出来——那就失去了"扫一眼"的作用。
/// </summary>
public record AppUsageRow(string Name, string DurationText, double Ratio, bool IsEntertainment)
{
    public string BadgeText => IsEntertainment ? "娱乐" : string.Empty;

    public bool HasBadge => IsEntertainment;
}

/// <summary>柱状图里的一根柱子。</summary>
public record BarItem(string Label, int Seconds);
