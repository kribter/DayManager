namespace DayManager.App.Services;

/// <summary>
/// 在 C# 里取颜色的统一入口。
///
/// 【为什么非要有这么一个类】
/// XAML 里可以写 <c>{AppThemeBinding Light=..., Dark=...}</c>，一条就跟着主题变。
/// 但这个软件有一批颜色是在 C# 里算出来的：气泡底色、状态徽章色、标题的灰、
/// 超时数字的红——它们拿不到那个绑定，只能自己判断当前是哪个主题。
/// 于是就出现了一种最难受的坏法：XAML 那一半跟着主题变了，
/// C# 这一半还停在浅色值上，界面变成深浅混搭，而且谁都不会报错。
///
/// 所以这里把"怎么判断主题"和"每个语义对应哪两个值"收在一处，
/// 两边读的是 Colors.xaml 里同一份定义。
///
/// 【为什么值来自资源字典，而不是在这里再抄一遍色号】
/// 抄一遍就等于有两个真值来源：改配色时要改两处，漏一处就又是深浅混搭。
/// 这里只存 key，真正的数值仍然在 Colors.xaml。
/// 取不到时（比如单元测试里没有 Application）退回传入的兜底色，不至于崩。
/// </summary>
public static class AppColors
{
    /// <summary>
    /// 当前是不是深色。
    ///
    /// 【为什么先看 UserAppTheme 而不是只看 RequestedTheme】
    /// RequestedTheme 是**系统**要的主题，UserAppTheme 是**用户在本软件里**选的。
    /// 多数人的系统是浅色，但编辑器、浏览器各自设成深色——
    /// 只跟系统走的话，这个软件会是他屏幕上唯一一块白的。
    /// 所以用户在软件里选了就听用户的，没选（Unspecified）才跟系统。
    /// </summary>
    public static bool IsDark => Application.Current?.UserAppTheme switch
    {
        AppTheme.Dark => true,
        AppTheme.Light => false,
        _ => Application.Current?.RequestedTheme == AppTheme.Dark,
    };

    private static Color Pick(string lightKey, string darkKey, Color fallbackLight, Color fallbackDark) =>
        Get(IsDark ? darkKey : lightKey, IsDark ? fallbackDark : fallbackLight);

    private static Color Get(string key, Color fallback)
    {
        if (Application.Current?.Resources.TryGetValue(key, out var value) == true && value is Color color)
        {
            return color;
        }

        return fallback;
    }

    /// <summary>
    /// 页面底色。
    ///
    /// 平时 XAML 直接用 AppThemeBinding 引用就行，这里留一份是给
    /// <b>窗口标题栏</b>用的：那条横条由系统绘制，只能从 C# 里按色值设，
    /// 而它必须和页面底色同为一色，否则深色窗口顶上留一条白边。
    /// </summary>
    public static Color PageBg => Pick("PageBgLight", "PageBgDark", Colors.White, Colors.White);

    /// <summary>正文。</summary>
    public static Color Text => Pick("TextPrimaryLight", "TextPrimaryDark", Colors.Black, Colors.White);

    /// <summary>
    /// 界面主色（按钮、高亮）。
    ///
    /// 【为什么连它也要在这里留一份】
    /// XAML 里到处写的是 <c>{StaticResource Primary}</c>，但画出来的东西
    /// （默认头像里那几格）拿不到静态资源，只能从 C# 里要。
    /// 不留这份就得在画的地方再抄一遍色号，于是又多出一个真值来源。
    /// </summary>
    public static Color Primary => Pick("Primary", "PrimaryDark", Color.FromArgb("#3F6FB5"), Color.FromArgb("#9DB8DE"));

    /// <summary>次要说明文字（副标题、单位、提示）。</summary>
    public static Color Muted => Pick("TextMutedLight", "TextMutedDark", Colors.Gray, Colors.LightGray);

    /// <summary>已经了结的事（完成、放弃）用的弱化色。</summary>
    public static Color Faint => Pick("TextFaintLight", "TextFaintDark", Colors.LightGray, Colors.DimGray);

    /// <summary>气泡上的文字。跟底色配对，不能跟正文走。</summary>
    public static Color OnBubble => Pick("TextOnBubbleLight", "TextOnBubbleDark", Colors.Black, Colors.White);

    public static Color Done => Pick("DoneGreen", "DoneGreenDark", Colors.ForestGreen, Colors.MediumSeaGreen);

    public static Color Progress =>
        Pick("ProgressOrange", "ProgressOrangeDark", Colors.DarkOrange, Colors.Orange);

    public static Color Warn => Pick("WarnOrange", "WarnOrangeDark", Colors.DarkOrange, Colors.Orange);

    public static Color Danger => Pick("DangerLight", "DangerDark", Colors.Firebrick, Colors.Salmon);

    /// <summary>自己说的气泡。</summary>
    public static Color BubbleSelf =>
        Pick("BubbleSelf", "BubbleSelfDark", Color.FromArgb("#DCEEFB"), Color.FromArgb("#2C465E"));

    /// <summary>AI 说的气泡。</summary>
    public static Color BubbleAi =>
        Pick("BubbleAi", "BubbleAiDark", Color.FromArgb("#F2F2F2"), Color.FromArgb("#2B2B2B"));
}
