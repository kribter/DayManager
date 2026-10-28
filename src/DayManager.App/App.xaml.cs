using Microsoft.Extensions.DependencyInjection;

#if WINDOWS
using DayManager.App.Platforms.Windows;
using DayManager.App.Services;
#endif

namespace DayManager.App;

public partial class App : Application
{
	public App()
	{
		InitializeComponent();
	}

	protected override Window CreateWindow(IActivationState? activationState)
	{
		// 界面深浅要在建窗口内容**之前**应用上：
		// 实测放在 MauiProgram.CreateMauiApp 里会被 WinUI 建窗口时按系统主题盖掉，
		// 而放在这里（窗口已建、还没显示）是真正生效的时机，
		// 且赶在第一帧排版之前，选了深色的人不会看见一帧白屏。
		if (MauiProgram.Services is { } services)
		{
			services.GetService<ThemeService>()?.ApplyFromStore();
		}

		var window = new Window(new AppShell());

#if WINDOWS
		// 托盘：窗口可以不见，采集和提醒不能停。
		//
		// 【为什么在这里挂，而不是界面某页里】
		// 它是"整个程序的去留"，不是某一页的功能；
		// 而且要在窗口出现之前就准备好——开机自启时窗口从头到尾都不该露面。
		// 第二个参数就是告诉它：这次是我自己起来的，别出来。
		MauiProgram.Services.GetService<IWindowShell>()?.Attach(window, WindowsAutoStart.IsLaunchedInBackground());

		// 标题栏染色：那条横条是系统画的，深色下会留一条白边。
		// 放在这里而不是在界面里，是因为它是整个窗口的事，不该由某个页面负责。
		WindowsTitleBar.Attach(window);

		// 关窗时把使用时长采集停掉。
		//
		// 【为什么不写也能用】
		// 采集每 5 秒就把结束时间推到此刻，所以就算被强杀，误差也只有 5 秒。
		// 这里做的是"礼貌收尾"：让最后一段有个干净的结束时间，
		// 也让退出路径上不留一个还在跑的定时器。
		window.Destroying += async (_, _) =>
		{
			try
			{
				var tracker = MauiProgram.Services.GetService<WindowsUsageTracker>();
				if (tracker is not null)
				{
					await tracker.StopAsync();
				}
			}
			catch
			{
				// 关窗路径上不能再抛异常：抛出去就是"关不掉"，
				// 而这件事对用户的价值远小于他要关掉程序的意愿。
			}
		};
#endif

		return window;
	}
}
