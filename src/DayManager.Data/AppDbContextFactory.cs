using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace DayManager.Data;

/// <summary>
/// 设计时 DbContext 工厂。
///
/// 【为什么需要这个类】
/// 执行 dotnet ef migrations 命令时，EF 工具必须先拿到一个 AppDbContext 实例才能工作。
/// 正常运行时，依赖注入容器会把配置好的 DbContext 递过来；
/// 但 Data 是个类库，既没有 Main 方法也没有容器，EF 工具不知道该怎么创建它。
/// 实现 IDesignTimeDbContextFactory 接口，等于明确告诉 EF 工具：
/// "没有容器的时候，用这个方法造一个出来。"
///
/// 【注意】它只在你敲迁移命令时起作用，程序真正运行时不会经过这里。
/// 运行时用哪个数据库文件，是由客户端 / 服务端各自启动时决定的。
/// </summary>
public class AppDbContextFactory : IDesignTimeDbContextFactory<AppDbContext>
{
    public AppDbContext CreateDbContext(string[] args)
    {
        // 这里的连接字符串只在生成迁移时用到，随便指向一个本地临时文件即可，
        // 因为迁移本身只是描述"表该怎么建"，不涉及真实数据。
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite("Data Source=daymanager.dev.db")
            .Options;

        return new AppDbContext(options);
    }
}
