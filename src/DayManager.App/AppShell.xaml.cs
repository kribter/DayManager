namespace DayManager.App;

public partial class AppShell : Shell
{
	public AppShell()
	{
		InitializeComponent();

		// 详情页注册成"普通页面"而不是 Tab：它不占标签栏的位置，
		// 但压进当前 Tab 的导航栈后自带返回按钮——
		// 进得去也出得来，比放在 XAML 里当 ShellContent 省心（见 AppShell.xaml 的说明）。
		Routing.RegisterRoute("ProjectDetailPage", typeof(ProjectDetailPage));

		// 设置页同理。它不占 Tab 是因为 Tab 的顺序就是优先级的表达，
		// 而设置不是工作流目的地——从时间页底部一行入口进来就够。
		Routing.RegisterRoute("SettingsPage", typeof(SettingsPage));
	}
}
