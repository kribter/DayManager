using Microsoft.Extensions.DependencyInjection;

#if WINDOWS
using DayManager.App.Platforms.Windows;
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
		var window = new Window(new AppShell());

#if WINDOWS
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
