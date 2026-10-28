namespace DayManager.App.Messages;

/// <summary>
/// 深浅主题被切换了。
///
/// 【为什么需要它】
/// XAML 里的 AppThemeBinding 由框架自动跟着变，不用管；
/// 但在 ViewModel 里算出来的颜色（气泡底色、状态徽章、标题的灰）不会——
/// 它们是赋值那一刻算出来的，主题换了值还是旧的，
/// 界面就会变成深浅混搭：卡片是深的、字是浅色主题下那个灰。
/// 收到这条消息的页面重新加载一遍，颜色就都重算了。
///
/// 跨 Tab 不能靠 OnAppearing：Shell 切 Tab 不保证触发它，
/// 而且用户切换主题时只在一个 Tab 上，其它 Tab 根本没被"进入"过。
/// </summary>
public sealed record ThemeChangedMessage;
