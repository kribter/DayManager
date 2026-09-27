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

		// 【为什么用 FileSystem.AppDataDirectory 而不是写死路径】
		// 写死路径（比如 "F:\data\app.db"）在手机上会因为没有这条盘符直接崩溃，
		// 而且各平台规定的应用数据目录完全不一样。这个属性会自动给出
		// 「本应用专属、有读写权限、卸载时随应用一起清掉」的正确目录。
		//
		// 【为什么又留了一个环境变量开关】
		// 标准目录虽然规范，但对个人自用有两个麻烦：路径深不好找、不方便备份。
		// 所以允许你用 DAYMANAGER_DATA_DIR 这个环境变量把库指定到任意位置，
		// 比如 F:\DayManager\data，甚至网盘同步目录。
		// 不设这个变量时行为完全不变，手机上永远走标准路径，两全其美。
		var customDir = Environment.GetEnvironmentVariable("DAYMANAGER_DATA_DIR");
		var dataDir = string.IsNullOrWhiteSpace(customDir)
			? FileSystem.AppDataDirectory
			: customDir;

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
