namespace DayManager.Core.Intervention;

using DayManager.Core.Enums;

/// <summary>
/// 内置的娱乐应用名单：**只放"一眼就知道是娱乐"的那些**。
///
/// 【为什么名单要克制到这种程度】
/// 名单是默认值，误判的代价不对称：
/// 漏掉一个游戏，顶多是它不被拦（用户可以自己标）；
/// 误把一个工具算成娱乐，则是在他正干活的时候弹"你已经用 Word 一小时了"——
/// 那种提醒一次就足以让人把整个功能关掉，而且再也不打开。
/// 所以浏览器、微信、音乐播放器一个都不放：它们全都既可能是娱乐也可能是正事。
///
/// 【为什么按进程名的基础名匹配，而不是写全 "bilibili.exe"】
/// 同一个软件的不同版本、绿色版、商店版，可执行文件名会在有 / 没有 .exe、
/// 大小写之间来回变。统一去掉 .exe 再小写比较，能少掉一整类"明明装了却认不出来"。
///
/// 【为什么这份名单放在 Core 而不是界面层】
/// 判定逻辑要能被冒烟测试直接验证。放在 App 里就只能靠实机点一遍——
/// 而"该拦的没拦"和"不该拦的拦了"这两种错误，实机测试都很难覆盖到。
/// </summary>
public static class EntertainmentApps
{
    /// <summary>
    /// 已知的娱乐应用（基础名，不带 .exe、小写）。
    ///
    /// 分组只是给读代码的人看的，匹配时不区分。
    /// </summary>
    private static readonly string[] Known =
    {
        // ---- 视频 / 短视频 ----
        "bilibili", "哔哩哔哩",
        "douyin", "抖音",
        "kuaishou", "快手",
        "ixigua", "西瓜视频",
        "qqlive", "txvideo",           // 腾讯视频
        "iqiyi", "qiyi",               // 爱奇艺
        "youku",                       // 优酷
        "mgtv",                        // 芒果 TV
        "netflix", "twitch",

        // ---- 直播 ----
        "douyu", "huya", "douyulive",

        // ---- 游戏平台（不含游戏本体：本体的进程名千奇百怪，列不完）----
        "steam", "steamwebhelper",
        "epicgameslauncher",
        "wegame",
        "battle.net", "battlenet",
        "origin", "eaapp", "eadesktop",
        "ubisoftconnect", "upc",
        "goggalaxy",

        // ---- 少数几个高置信度的游戏本体 ----
        // 只放进程名稳定的那些。绝大多数游戏的进程名跟安装目录、语言版本有关，
        // 列不全，列了也容易过期——所以名单的定位始终是"打底"，不是"覆盖"。
        "genshinimpact", "yuanshen", "starrail", "honkai",
        "leagueoflegends", "cs2", "dota2",

        // ---- 社区 / 消遣 ----
        "weibo", "xiaohongshu", "rednote", "tieba"
    };

    private static readonly HashSet<string> Index =
        new(Known.Select(Normalize), StringComparer.OrdinalIgnoreCase);

    /// <summary>名单里有多少条（冒烟测试用，也方便在设置页说明"内置了 N 个"）。</summary>
    public static int Count => Index.Count;

    /// <summary>
    /// 这个进程名是不是内置名单里的娱乐应用。
    ///
    /// 传空、传路径都能安全处理：取不到名字就不猜（返回 false），
    /// 猜错的方向必须是"不拦"，见上面那段关于代价不对称的说明。
    /// </summary>
    public static bool IsEntertainment(string? appId)
    {
        if (string.IsNullOrWhiteSpace(appId))
        {
            return false;
        }

        return Index.Contains(Normalize(appId));
    }

    /// <summary>
    /// 结合"用户标记"和"内置名单"，给出这个应用最终算什么。
    ///
    /// 【为什么这个优先级不能反过来】
    /// 名单是猜的，标记是用户说的。反过来的话，用户把 B 站标成工作，
    /// 下一次判定又被名单重新算成娱乐——他会觉得这个开关是坏的，而且找不到原因。
    ///
    /// 【为什么这个函数放在 Core，而不是留在时间页的 ViewModel 里】
    /// 同一个判断有两个消费者：干预引擎（要不要拦）和时间页（徽章显示什么）。
    /// 两处各写一遍，迟早出现"时间页写着娱乐、干预却不拦"。
    /// 放进 Core 之后，冒烟测试也能直接验证这个优先级。
    /// </summary>
    public static AppKind Classify(string appId, AppKind marked)
    {
        if (marked != AppKind.Neutral)
        {
            return marked;
        }

        return IsEntertainment(appId) ? AppKind.Entertainment : AppKind.Neutral;
    }

    /// <summary>去掉 .exe、去掉路径、转小写。</summary>
    private static string Normalize(string appId)
    {
        var name = appId.Trim();

        // 万一传进来的是完整路径（C:\Program Files\...\bilibili.exe），先取文件名。
        var slash = name.LastIndexOfAny(new[] { '\\', '/' });
        if (slash >= 0)
        {
            name = name[(slash + 1)..];
        }

        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            name = name[..^4];
        }

        return name.ToLowerInvariant();
    }
}
