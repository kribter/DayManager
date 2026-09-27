using DayManager.Core.Entities;
using Microsoft.EntityFrameworkCore;

namespace DayManager.Data;

/// <summary>
/// 数据库上下文（客户端与服务端共用）。
///
/// 【为什么两端共用同一个 DbContext】
/// 这是本项目省事最多的决定之一。表结构、字段、索引只有这一份代码，
/// 于是"手机上的库和电脑上的库字段不一致"这种问题从根本上不存在了。
/// 日后要加字段，改一处，两端 migration 自动同步。
///
/// 【为什么用 Fluent API 而不是在实体上写 [Key] 这种注解】
/// 注解会让 Core 层被迫依赖 EF Core 的命名空间，那样 Core 就"不纯"了。
/// 保持 Core 零依赖，意味着将来任何端（甚至一个 Python 脚本）都能直接引用这份模型定义。
/// 这叫"约定分离"：实体只管是什么，怎么存是数据层的事。
/// </summary>
public class AppDbContext : DbContext
{
    /// <summary>日程与待办（统一记录）。</summary>
    public DbSet<Item> Items => Set<Item>();

    /// <summary>项目（两级结构的上层）。</summary>
    public DbSet<Project> Projects => Set<Project>();

    /// <summary>设备使用会话。</summary>
    public DbSet<UsageSession> UsageSessions => Set<UsageSession>();

    /// <summary>每日上下文与统计快照。</summary>
    public DbSet<DailyContext> DailyContexts => Set<DailyContext>();

    /// <summary>
    /// 构造函数接收 options，由调用方决定连哪个库（客户端连本机 SQLite，服务端连服务器上的库）。
    /// 这种写法叫依赖注入：DbContext 自己不关心连哪，交给外部配置。
    /// </summary>
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options)
    {
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // 四个表的配置写在下面四个私有方法里。
        // 【为什么不拆成四个 IEntityTypeConfiguration 类】
        // 只有四张表时，拆成四个文件反而要来回跳转才能看全。集中在这里更好读，
        // 等表真的多到看不过来再拆也不迟。
        ConfigureItem(modelBuilder.Entity<Item>());
        ConfigureProject(modelBuilder.Entity<Project>());
        ConfigureUsageSession(modelBuilder.Entity<UsageSession>());
        ConfigureDailyContext(modelBuilder.Entity<DailyContext>());
    }

    private static void ConfigureItem(Microsoft.EntityFrameworkCore.Metadata.Builders.EntityTypeBuilder<Item> b)
    {
        b.ToTable("items");
        b.HasKey(x => x.Id);

        // HasMaxLength 在 SQLite 上其实不生效（SQLite 不强制长度限制），
        // 但保留它是为了将来万一换成 PostgreSQL 时约束依然有效，且能提前发现过长的脏数据。
        b.Property(x => x.Title).IsRequired().HasMaxLength(200);
        b.Property(x => x.Notes).HasMaxLength(4000);
        b.Property(x => x.TimeZoneId).IsRequired().HasMaxLength(64).HasDefaultValue("Asia/Shanghai");
        b.Property(x => x.MinimalStep).HasMaxLength(500);
        b.Property(x => x.RRule).HasMaxLength(500);
        b.Property(x => x.Tags).HasMaxLength(1000);
        b.Property(x => x.Reminders).HasMaxLength(1000);

        // ---------- 关系 ----------

        /// 项目被删掉时，它下面的任务不应该跟着消失（那会连累历史完成率统计），
        /// 所以这里用 SetNull：只把 ProjectId 置空，任务本身变成零散任务保留下来。
        b.HasOne(x => x.Project)
            .WithMany(p => p.Items)
            .HasForeignKey(x => x.ProjectId)
            .OnDelete(DeleteBehavior.SetNull);

        // 重复日程的自引用：覆盖记录指向母记录。
        // ClientSetNull 而不是 Cascade，避免删母记录时把用户手改过的单次数据一起删掉。
        b.HasOne<Item>()
            .WithMany()
            .HasForeignKey(x => x.RecurrenceParentId)
            .OnDelete(DeleteBehavior.ClientSetNull);

        // ---------- 索引 ----------

        // 【为什么这些地方要建索引】
        // 日视图每次打开都要查"某一天的未完成条目"，这个查询会天天跑无数次。
        // 没有索引的话，数据量上万后每次都是全表扫描，手机上能感觉到明显卡顿。
        b.HasIndex(x => new { x.StartAt, x.Status });
        b.HasIndex(x => x.DueAt);              // 待办按截止时间排序
        b.HasIndex(x => x.Status);             // 完成率统计
        b.HasIndex(x => x.ProjectId);          // 项目进度查询
        b.HasIndex(x => x.RecurrenceParentId); // 展开重复日程时找覆盖记录
        b.HasIndex(x => x.UpdatedAt);          // 增量同步的核心索引

        // 软删除全局过滤：普通查询自动看不见被删的记录。
        //
        // 【同步时怎么办】
        // 服务端必须能把墓碑记录查出来发给其他设备，否则删除指令传不出去。
        // 解决办法是在同步代码里调用 IgnoreQueryFilters() 临时关掉这个过滤。
        b.HasQueryFilter(x => x.DeletedAt == null);
    }

    private static void ConfigureProject(Microsoft.EntityFrameworkCore.Metadata.Builders.EntityTypeBuilder<Project> b)
    {
        b.ToTable("projects");
        b.HasKey(x => x.Id);

        b.Property(x => x.Name).IsRequired().HasMaxLength(200);
        b.Property(x => x.Description).HasMaxLength(2000);
        b.Property(x => x.Color).HasMaxLength(16);

        // 日常界面几乎总是"列出所有未归档项目"，这个索引避免每次全表扫。
        b.HasIndex(x => x.Status);
        b.HasIndex(x => x.UpdatedAt);

        b.HasQueryFilter(x => x.DeletedAt == null);
    }

    private static void ConfigureUsageSession(Microsoft.EntityFrameworkCore.Metadata.Builders.EntityTypeBuilder<UsageSession> b)
    {
        b.ToTable("usage_sessions");
        b.HasKey(x => x.Id);

        b.Property(x => x.AppId).IsRequired().HasMaxLength(300);
        b.Property(x => x.AppName).HasMaxLength(300);
        b.Property(x => x.WindowTitle).HasMaxLength(1000);

        // 【这张表的查询模式决定了必须这么建索引】
        // 典型查询是"某台设备上，某个时间段内的所有会话"——用于按天聚合统计。
        // 所以索引是 (StartAt, EndAt)，让范围扫描走索引而不是全表。
        b.HasIndex(x => new { x.StartAt, x.EndAt });
        b.HasIndex(x => x.AppId);
        b.HasIndex(x => x.IsEntertainment);   // 快速算出今天娱乐了多久

        b.HasQueryFilter(x => x.DeletedAt == null);
    }

    private static void ConfigureDailyContext(Microsoft.EntityFrameworkCore.Metadata.Builders.EntityTypeBuilder<DailyContext> b)
    {
        b.ToTable("daily_contexts");
        b.HasKey(x => x.Id);

        // 【为什么给 Date 建唯一索引】
        // 一天只能有一条上下文记录。靠代码判断"今天有没有"再去 Decide 新增还是更新，
        // 在两台设备同时操作时会有竞态（都发现没有，于是都插入）。
        // 数据库层面的唯一约束是唯一可靠的兜底。
        b.HasIndex(x => x.Date).IsUnique();

        b.HasIndex(x => x.UpdatedAt);

        b.HasQueryFilter(x => x.DeletedAt == null);
    }

    /// <summary>
    /// 在保存前统一维护同步所需的元数据。
    ///
    /// 【为什么要重写 SaveChanges，而不是在业务代码里手动写 version++】
    /// 只要有一个地方忘记累加版本号，同步时就会误判（因为版本没变，被认为没改过），
    /// 这类 bug 极其隐蔽。集中在这里处理，任何修改路径都逃不掉，且只需要写一次。
    /// </summary>
    private void ApplySyncMetadata()
    {
        var now = DateTimeOffset.UtcNow;

        // ChangeTracker 是 EF Core 的变更跟踪器，它会记录本次操作涉及哪些实体的什么变化。
        foreach (var entry in ChangeTracker.Entries<SyncEntityBase>())
        {
            switch (entry.State)
            {
                case EntityState.Added:
                    entry.Entity.UpdatedAt = now;
                    // 新建时版本号从 1 开始。允许外部显式指定（比如从服务端同步过来的数据），
                    // 所以只在没设过的情况下赋初值。
                    if (entry.Entity.Version <= 0)
                    {
                        entry.Entity.Version = 1;
                    }
                    break;

                case EntityState.Modified:
                    // 软删除（设置 DeletedAt）本质上也是一次修改，所以同样会走到这里，
                    // 版本号自然递增，"删除"这个动作就能被同步系统正确感知。
                    entry.Entity.Version++;
                    entry.Entity.UpdatedAt = now;
                    break;
            }
        }
    }

    public override int SaveChanges()
    {
        ApplySyncMetadata();
        return base.SaveChanges();
    }

    public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        ApplySyncMetadata();
        return base.SaveChangesAsync(cancellationToken);
    }
}
