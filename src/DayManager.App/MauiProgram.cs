using DayManager.App.ViewModels;
using DayManager.Data;
using DayManager.Data.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace DayManager.App;

public static class MauiProgram
{
	public static MauiApp CreateMauiApp()
	{
		var builder = MauiApp.CreateBuilder();
		builder
			.UseMauiApp<App>()
			.ConfigureFonts(fonts =>
			{
				fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
				fonts.AddFont("OpenSans-Semibold.ttf", "OpenSansSemibold");
			});

		// ================= 数据库注册 =================

		// 【Windows 端固定放在项目下的 data\，手机端走系统标准目录】
		//
		// 为什么 Windows 端可以写死：这是给你自己用的程序，项目位置不会变。
		// 写死之后，不管你是双击 start.bat、直接点 exe、还是在 Visual Studio 里按 F5，
		// 用的都是同一个数据库。
		//
		// 【这里删掉过一个设计，值得记住为什么】
		// 原先是读环境变量 DAYMANAGER_DATA_DIR，设了就用环境变量、没设就用默认目录。
		// 看起来更灵活，实际是个坑：从 bat 启动（有变量）和直接点 exe（没变量）
		// 会落到两个不同的库，表现为「清单突然空了」，第一反应还以为数据丢了。
		// 结论：这种基础配置，单一来源比灵活重要得多，能写死就写死。
		//
		// 为什么手机端不能写死：Android 上没有 F: 这种盘符，往任意目录写文件也会被系统拒绝。
		// FileSystem.AppDataDirectory 会给出「本应用专属、有读写权限」的正确目录。
		//
		// 为什么用运行时判断而不是 #if WINDOWS：编译符号的名字容易记错，
		// 写错了不会报错，只会静默走错分支——运行时判断没有这个风险。
		var dataDir = OperatingSystem.IsWindows()
			? @"F:\DayManager\data"
			: FileSystem.AppDataDirectory;

		// 目录不存在就先建出来，避免 SQLite 报「找不到路径」这种低级错误
		if (!Directory.Exists(dataDir))
		{
			Directory.CreateDirectory(dataDir);
		}

		var dbPath = Path.Combine(dataDir, "daymanager.db");

		// 【为什么注册的是"工厂"而不是直接注册 DbContext】
		// AppDbContext 内部会缓存它查过的数据，如果整个程序共用一个实例，
		// 缓存会越来越大，而且它不允许同时被两处代码使用（不是线程安全的），
		// 后台采集线程一起用时很容易报并发错误。
		// 用工厂的话，每次操作现造一个短命实例，用完就释放，是最稳妥的用法。
		builder.Services.AddDbContextFactory<AppDbContext>(options =>
			options.UseSqlite($"Data Source={dbPath}"));

		// ================= 业务服务注册 =================

		// 【生命周期的三种选择：Singleton / Scoped / Transient】
		//   Singleton：整个程序共用一个实例
		//   Scoped   ：一次"作用域"内共用一个（Web 里通常是一次请求）
		//   Transient：每次要都新建一个
		// 这里的选择依据是这个对象自己持不持有状态：
		//   ItemRepository 只拿着数据库工厂，不保存任何业务数据 → Singleton 没问题，省去反复创建
		//   MainViewModel 会保存一堆当前状态（今天列表、输入框内容）→ 用 Transient，
		//     这样每次打开页面都是干净的，不会带着上一次的旧数据
		builder.Services.AddSingleton<ItemRepository>();
		builder.Services.AddTransient<MainViewModel>();

		// 页面也要注册，因为 AppShell 是通过 DataTemplate 让 DI 容器来创建 MainPage 的。
		// 不注册的话，容器会尝试找无参构造函数 → MainPage 又需要一个 MainViewModel → 直接崩。
		// 这是用 Shell + DI 时最容易漏的一步。
		builder.Services.AddTransient<MainPage>();

#if DEBUG
		builder.Logging.AddDebug();
#endif

		var app = builder.Build();

		InitializeDatabase(app.Services);

		return app;
	}

	/// <summary>
	/// 启动时确保数据表存在。
	///
	/// 【为什么放在这里自动执行，而不是手动敲命令】
	/// 你不用每次换设备都记得去执行迁移命令，程序一起来就保证表结构是最新的。
	/// Migrate 只会执行还没跑过的迁移，重复启动不会重建数据、不会丢东西。
	///
	/// 【这里用了同步调用，严格来说不够完美】
	/// 更好的做法是把初始化做成异步并显示加载动画，
	/// 但首次启动时表里没几条数据，通常在几十毫秒内就完成了，先保持简单。
	/// </summary>
	private static void InitializeDatabase(IServiceProvider services)
	{
		// CreateScope 相当于划定一小段生命周期范围：
		// 这次操作要用的临时服务在这个范围结束后自动释放。
		using var scope = services.CreateScope();

		var dbFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<AppDbContext>>();
		using var db = dbFactory.CreateDbContext();

		db.Database.Migrate();
	}
}
