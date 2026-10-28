using DayManager.App.Services;
using DayManager.App.ViewModels;
using DayManager.Core.Intervention;
using DayManager.Data;
using DayManager.Data.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

#if WINDOWS
using DayManager.App.Platforms.Windows;
#endif

namespace DayManager.App;

public static class MauiProgram
{
	/// <summary>
	/// 程序的服务容器。
	///
	/// 【为什么需要它】
	/// 生命周期回调（窗口关闭）里拿不到依赖注入，只能从容器里手工取。
	/// 目前只有"退出时停掉使用时长采集"这一处用它，多一处都算设计走味。
	/// </summary>
	public static IServiceProvider Services { get; private set; } = null!;

	public static MauiApp CreateMauiApp()
	{
		// 越早挂上越好：初始化阶段崩掉的话（数据库迁移、资源字典加载），
		// 挂晚了那一批异常一个都记不到。
		CrashLogger.Init();

#if WINDOWS
		// 单实例：必须在起来任何东西之前判断。
		// 放晚了，第二个实例已经启动了自己的采集器和提醒引擎——
		// 那时候再退出，用户刚才那一下双击已经在他不知道的地方造成影响了。
		if (!SingleInstance.TryBecomeOwner())
		{
			// 已经在跑了：把它叫出来，然后这一份就到此为止。
			SingleInstance.FocusExisting();

			// 这里不能优雅地返回：调 CreateMauiApp 的那条链路还在等着一个 App 实例。
			// 进程到此终止，没有任何窗口、没有任何定时器被创建过。
			Environment.Exit(0);
		}
#endif

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

		// 读写不再互斥（见 SqliteBootstrap 的说明）。
		// 必须在任何连接打开之前做：采集器每 5 秒写一次库，
		// 时间页刷新要读一整天——默认模式下这两件事会互相挡。
		DayManager.Data.SqliteBootstrap.EnableWal(dbPath);

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
		builder.Services.AddSingleton<ProjectRepository>();

		// 对话记录：关掉程序也要留着（见 ChatMessage 的说明）。
		builder.Services.AddSingleton<ChatRepository>();

		// 设备使用时长（M7）：读写与统计都在共享代码里，两端以后一起用。
		builder.Services.AddSingleton<UsageRepository>();

		// 干预参数与"今天别再提了"（M9）。参数存库是因为它要能被 AI 动态校准，
		// 而且改完必须跨重启有效——只放内存的话，"今天别再提"过一夜又忘了。
		builder.Services.AddSingleton<PolicyRepository>();
		builder.Services.AddSingleton<AppRuleRepository>();

#if WINDOWS
		// 采集器只有 Windows 一份实现（全靠 user32.dll）。
		// 【这里为什么用编译符号而不是像数据库路径那样运行时判断】
		// 因为 WindowsUsageTracker 这个类型在 Android 上根本不存在，
		// 运行时判断连编译都过不去。这是唯一必须用 #if 的场合。
		builder.Services.AddSingleton<WindowsUsageTracker>();

		// 采集器同时是"现在在用什么"的来源。注册成接口，
		// 是为了让干预服务（共享代码）不必知道 Windows 这个类。
		builder.Services.AddSingleton<ICurrentUsageSource>(sp =>
			sp.GetRequiredService<WindowsUsageTracker>());

		// 系统通知。
		builder.Services.AddSingleton<WindowsNotifier>();
		builder.Services.AddSingleton<IInterventionNotifier>(sp =>
			sp.GetRequiredService<WindowsNotifier>());

		// 干预引擎。只在有采集器和通知的平台上注册——
		// 它的两个输入缺一个，功能就只能是个空转的定时器。
		builder.Services.AddSingleton<InterventionService>();

		// 「先做 10 分钟」的计时器（启动辅助）。
		// 它到点要靠系统通知说话，所以跟干预引擎一样只在有通知实现的平台上注册；
		// 没有它的时候 MainViewModel 拿到的是 null，界面上那两块自然不出现。
		builder.Services.AddSingleton<FocusSessionService>();

		// 托盘（藏起来但继续跑）与开机自启。
		// 这两样也是纯平台能力，界面层对着接口说话。
		builder.Services.AddSingleton<IWindowShell, WindowsTray>();
		builder.Services.AddSingleton<IAutoStartService, WindowsAutoStart>();
#endif

		// 任务的「更多」菜单：日程页和大任务详情页共用同一份实现，
		// 所以它是单例（自己不存任何状态，只拿着仓储）。
		builder.Services.AddSingleton<ItemActionService>();
		builder.Services.AddTransient<MainViewModel>();

		// ================= 后端访问 =================

		// 服务器地址与令牌：进程内只有一份，任何地方改了都立刻生效。
		// 用工厂重载（而不是 AddSingleton<ServerSettings>()）是因为要先从
		// Preferences / 本地种子文件把值读出来，不能直接 new 一个空的。
		builder.Services.AddSingleton(_ => ServerSettings.Load());

		// 【为什么这里手写 new HttpClient，而不像后端那样用 AddHttpClient】
		// AddHttpClient 属于 Microsoft.Extensions.Http，是 Web 那一套的组件，
		// MAUI 项目默认不带。这里只需要一个客户端实例，手写反而更清楚。
		//
		// 【UseProxy = false 是必须的，这里踩过真实的坑】
		// 默认情况下 HttpClient 会读取系统的代理设置。一旦机器上配了代理或开着 VPN，
		// 发往 127.0.0.1 的请求也会被送进代理。
		// 实测后果：往一个根本没监听的端口发请求，居然收到了 200，
		// 而且响应头里是 Server: Kestrel——代理把请求转给了真正的后端。
		// 表现就是"服务器地址明明写错了却连得上"，排查时会被带得完全跑偏。
		// 后端永远在本机或局域网，没有任何理由让它经过代理。
		//
		// 超时给 200 秒：AI 拆一整批任务本来就要跑一分多钟，
		// 给短了会把正常的慢请求掐断。探测接口不用这个值——
		// 它在 ApiClient 里单独设了 3 秒超时，见那里的说明。
		builder.Services.AddSingleton(new HttpClient(new HttpClientHandler
		{
			UseProxy = false
		})
		{
			Timeout = TimeSpan.FromSeconds(200)
		});

		builder.Services.AddSingleton<ApiClient>();

		// 客户端自己把后端拉起来。
		// 地址变化时（比如以后换成 Tailscale 的 100.x 地址）它会自动变成空壳不再启动，
		// 因为那时后端跑在另一台机器上，本机启动它没有意义。
		builder.Services.AddSingleton(provider =>
			BackendLauncher.Create(
				provider.GetRequiredService<ServerSettings>().BaseUrl,
				SeedFileReader.RepoRoot));

		// 任务拆解：把一件模糊的大事拆成能照着做的每日计划。
		// 无状态（它只是拿着 ApiClient 去问 AI），所以做成单例。
		builder.Services.AddSingleton<TaskDecomposer>();

		// 说话之前先确保后端在，不在就自己拉起来。对话页和拆解页共用同一套逻辑。
		builder.Services.AddSingleton<BackendGuard>();

		// 页面也要注册，因为 AppShell 是通过 DataTemplate 让 DI 容器来创建 MainPage 的。
		// 不注册的话，容器会尝试找无参构造函数 → MainPage 又需要一个 MainViewModel → 直接崩。
		// 这是用 Shell + DI 时最容易漏的一步。
		builder.Services.AddTransient<MainPage>();
		builder.Services.AddTransient<PlanPage>();
		builder.Services.AddTransient<PlanViewModel>();

		// 对话页是核心界面，默认落在它上面（决策 D12）。
		builder.Services.AddTransient<ChatPage>();
		builder.Services.AddTransient<ChatViewModel>();

		// 大任务（M6）：列表页 + 详情页。
		// 用 Transient 是因为它们带着当前项目的状态，每次导航进来都该是干净的。
		builder.Services.AddTransient<ProjectsPage>();
		builder.Services.AddTransient<ProjectsViewModel>();
		builder.Services.AddTransient<ProjectDetailPage>();
		builder.Services.AddTransient<ProjectDetailViewModel>();

		// 时间页（M8）：使用统计。
		builder.Services.AddTransient<TimePage>();
		builder.Services.AddTransient<TimeViewModel>();

#if DEBUG
		builder.Logging.AddDebug();
#endif

		var app = builder.Build();

		Services = app.Services;

		InitializeDatabase(app.Services);

#if WINDOWS
		StartUsageTracking(app.Services);
		StartIntervention(app.Services);
		WarmUpBackend(app.Services);
#endif

		return app;
	}

#if WINDOWS
	/// <summary>
	/// 开始采集使用时长。
	///
	/// 【为什么不在界面上放一个"开始记录"的开关】
	/// 人要记录自己的时间，还得先想起来去点一下开始——那这份数据永远缺最该记的那几天。
	/// 采集随程序启动，不需要用户做任何动作。
	///
	/// 【为什么扔到后台线程】
	/// 第一次采样会读库、读进程信息，放在启动路径上会让窗口晚出来半秒。
	/// 采集本来就是个记录员，不该挡在用户和界面之间。
	/// </summary>
	private static void StartUsageTracking(IServiceProvider services)
	{
		var tracker = services.GetRequiredService<WindowsUsageTracker>();

		_ = Task.Run(async () =>
		{
			try
			{
				await tracker.StartAsync();
			}
			catch (Exception ex)
			{
				// 采集挂了不该让程序起不来：用户的日程才是主角。
				// 但要留下痕迹，否则"时间页一直是空的"会被当成界面 bug 查半天。
				CrashLogger.Write("使用时长采集启动失败", ex);
			}
		});
	}

	/// <summary>
	/// 启动时就把后端确保在跑。
	///
	/// 【为什么要加这一段】
	/// 原先后端只在"打开对话页/拆解页"时才被拉起来，因为那是唯一用到它的地方。
	/// 加上后台常驻之后这条就不成立了：开机自启是 <c>--background</c> 启动的，
	/// 窗口从头到尾不露面，也就没有任何机会打开那两个页面——
	/// 结果是"电脑开了一整天，AI 一次都没连上"。
	/// 马上要做的 AI 主动提醒，恰恰是要在没人碰界面的时刻说话的，缺了后端就只能干等着。
	///
	/// 【为什么放在 client 启动路径里，而不是写成开机自启】
	/// 还是那个原则：由客户端负责把它拉起来，不要让它永远常驻。
	/// 区别只在于把"要的时候"从"打开对话页"提前到"客户端一启动"。
	///
	/// 【为什么扔到后台线程】
	/// 它内部最多要等 20 秒。放在启动路径上就是"开个程序要等半天"，
	/// 而这期间界面上并不缺它（日程、时间页、干预全是本地的）。
	/// 与此同时窗口照常出来，用户在对话页发出的第一个请求
	/// 会自己再走一遍 BackendGuard —— 那时后端多半已经好了。
	///
	/// 【为什么失败了只写日志】
	/// 后端挂了不该让客户端起不来，用户的日程才是主角。
	/// 对话页那边的 EnsureAsync 还会再给一次机会，也会把"连不上"显示出来。
	/// </summary>
	private static void WarmUpBackend(IServiceProvider services)
	{
		_ = Task.Run(async () =>
		{
			try
			{
				await services.GetRequiredService<BackendGuard>().EnsureAsync();
			}
			catch (Exception ex)
			{
				CrashLogger.Write("后端预热失败", ex);
			}
		});
	}

	/// <summary>
	/// 启动干预引擎。
	///
	/// 【为什么在这里同步调用，而不是像采集那样扔到后台线程】
	/// 它启动时要做两件必须在 UI 线程上的事：注册通知通道（COM 注册），
	/// 以及把"现在在干嘛"这句话交给界面绑定。
	/// 这两件事加起来是几十毫秒，放在启动路径上用户察觉不到；
	/// 而扔到后台线程反而要处理"界面已经准备好、引擎还没注册好"的竞态。
	///
	/// 轮询本身还是走定时器、在后台跑，不会占住启动。
	/// </summary>
	private static void StartIntervention(IServiceProvider services)
	{
		try
		{
			services.GetRequiredService<InterventionService>().Start();
		}
		catch (Exception ex)
		{
			// 提醒坏了不该影响日程本身，但原因必须留下：
			// 用户看到的是"一条提醒都没有"，排查第一句就是"它启动了吗"。
			CrashLogger.Write("干预引擎启动失败", ex);
		}
	}
#endif

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
