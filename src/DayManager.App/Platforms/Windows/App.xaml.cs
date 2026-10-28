using Microsoft.UI.Xaml;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace DayManager.App.WinUI;

/// <summary>
/// Provides application-specific behavior to supplement the default Application class.
/// </summary>
public partial class App : MauiWinUIApplication
{
	/// <summary>
	/// Initializes the singleton application object.  This is the first line of authored code
	/// executed, and as such is the logical equivalent of main() or WinMain().
	/// </summary>
	public App()
	{
		this.InitializeComponent();

		// XAML 解析失败、绑定失败、界面事件里的异常最先冒到这里。
		// 不接的话它们会一路往上变成"窗口直接消失"，连堆栈都拿不到。
		this.UnhandledException += (_, e) =>
		{
			DayManager.App.Services.CrashLogger.Write("界面异常", e.Exception);

			// 先记下来再说。设成已处理，程序就不会当场消失——
			// 闪退的话连堆栈都来不及写，而"窗口没了"对排查没有任何帮助。
			e.Handled = true;
		};
	}

	protected override MauiApp CreateMauiApp() => MauiProgram.CreateMauiApp();
}

