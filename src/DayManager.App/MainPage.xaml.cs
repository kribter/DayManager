using DayManager.Core.Entities;
using DayManager.Data;
using Microsoft.EntityFrameworkCore;

namespace DayManager.App;

public partial class MainPage : ContentPage
{
	private readonly IDbContextFactory<AppDbContext> _dbFactory;

	/// <summary>
	/// 【为什么构造函数要声明一个参数】
	/// 这叫构造函数注入：页面只声明"我需要数据库能力"，
	/// 具体实例由 MauiProgram 里注册好的容器自动送过来。
	/// 好处是页面完全不必知道数据库文件在哪、怎么连接，
	/// 以后想换存储方式，改注册那一处就够了。
	///
	/// 【为什么不直接 new AppDbContext()】
	/// 自己 new 的话连接字符串就写死在页面里了，
	/// 而且失去了统一的生命周期管理，是后期很难收拾的典型写法。
	/// </summary>
	public MainPage(IDbContextFactory<AppDbContext> dbFactory)
	{
		InitializeComponent();
		_dbFactory = dbFactory;
		RefreshStats();
	}

	private void RefreshStats()
	{
		try
		{
			// 每次操作现开一个短命的 DbContext，用完就释放。
			// using 的作用是保证离开这个花括号时自动 Dispose，把数据库连接还回去。
			using var db = _dbFactory.CreateDbContext();

			// 把真实的数据库文件路径显示出来，方便你在 M0 阶段确认库确实建出来了
			var path = db.Database.GetDbConnection().DataSource;

			var itemCount = db.Items.Count();
			var projectCount = db.Projects.Count();

			StatsLabel.Text = $"已有 {itemCount} 条记录，{projectCount} 个项目";
			DbPathLabel.Text = path;
		}
		catch (Exception ex)
		{
			// 【为什么数据库操作要用 try-catch 包住】
			// 数据库相关的异常（文件损坏、磁盘满、权限被系统回收）在真机上是会发生的，
			// 尤其在 Android 上。把原因显示到界面上，比程序一声不响崩掉要好查得多。
			StatsLabel.Text = "数据库未就绪";
			DbPathLabel.Text = ex.Message;
		}
	}

	/// <summary>
	/// 按钮点击事件。
	/// async void 基本只用在 UI 事件处理器上（其他地方都应写成 async Task），
	/// 因为事件本身没有返回值，也就不存在一个可以等待的 Task。
	///
	/// 【为什么 sender 要写成 object? 而不是 object】
	/// 这是 C# 的"可空引用类型"机制：EventHandler 委托把 sender 声明成了可能为 null，
	/// 我们的实现必须保持一致，否则编译器会警告两边约定不符。
	/// 加一个问号表示"我清楚它有可能是 null，并且我不会直接解引用它"。
	/// </summary>
	private async void OnAddTestClicked(object? sender, EventArgs e)
	{
		using var db = await _dbFactory.CreateDbContextAsync();

		// 注意这里没有给 Version 赋值，也没有写 UpdatedAt。
		// 这两件事在 AppDbContext.ApplySyncMetadata 里统一处理了，
		// 任何地方的新增或修改都会自动带上，不必担心遗漏导致同步出错。
		db.Items.Add(new Item
		{
			Title = $"测试日程 {DateTime.Now:HH:mm:ss}",
			StartAt = DateTimeOffset.Now,
			DueAt = DateTimeOffset.Now.AddHours(2),
			MinimalStep = "先打开文档写一句开场"
		});

		await db.SaveChangesAsync();

		RefreshStats();
	}
}
