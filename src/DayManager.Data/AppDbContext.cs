using DayManager.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

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
    /// 写操作日志。**撤销的基础**，永久保留，不清理。
    /// </summary>
    public DbSet<ActionLog> ActionLogs => Set<ActionLog>();

    /// <summary>会自己变的参数（见需求文档 2.7）。</summary>
    public DbSet<PolicySetting> PolicySettings => Set<PolicySetting>();

    /// <summary>
    /// 与 AI 的对话记录。**关掉程序也要留着**，见 ChatMessage 的说明。
    /// </summary>
    public DbSet<ChatMessage> ChatMessages => Set<ChatMessage>();

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
        ConfigureActionLog(modelBuilder.Entity<ActionLog>());
        ConfigurePolicySetting(modelBuilder.Entity<PolicySetting>());
        ConfigureChatMessage(modelBuilder.Entity<ChatMessage>());

        ApplyTimeConverters(modelBuilder);
    }

    // ==================================================================
    // 时间类型的存储方式
    //
    // 【背景：这里踩了一个真实的坑】
    // EF Core 的 SQLite 提供程序不支持直接比较 DateTimeOffset。
    // 也就是说，下面这种看起来完全正常的查询，编译毫无问题，运行时却直接抛异常：
    //
    //     db.Items.Where(x => x.StartAt >= 今天零点 && x.StartAt < 明天零点)
    //     → InvalidOperationException: The LINQ expression could not be translated
    //
    // 而日程软件的全部核心查询（查今天、查本周、查某个时间段）都建立在时间范围比较上，
    // 所以这不是一个能绕开的小问题，必须在模型层彻底解决。
    //
    // 【解法：换成整数存储】
    // 把 DateTimeOffset 统一转成"距 1970 年的毫秒数"（Unix 毫秒）存成 INTEGER。
    // 好处有三：
    //   1. 整数可以直接比大小，SQL 翻译不再有任何障碍；
    //   2. 它表示的是绝对时刻，天然跨时区正确。
    //      之前直接存带偏移的文本（"2026-09-27 09:00:00+08:00"）时，
    //      一旦手机和电脑处于不同时区，连"谁在前谁在后"都可能判错；
    //   3. 体积小、索引效率高。
    //
    // 【代价是什么】
    // 数据库文件里这一列是数字，用 SQLite 浏览器直接看不出日期。
    // 需要肉眼查看时可以这么查：
    //     SELECT datetime(StartAt/1000, 'unixepoch', 'localtime') FROM items;
    // 另外读出来的 DateTimeOffset 偏移量是 0（UTC），
    // 显示到界面前必须调用 .ToLocalTime() 转成本地时间——这一步千万别漏。
    //
    // 【为什么现在改代价最小】
    // 数据库里目前只有一条测试记录。这类存储方案的修正越晚越贵：
    // 等真实数据攒了半年再改，就得写一条数据迁移脚本，
    // 还得承担"迁移过程中数据出错"的风险。所以现在改正是时候。
    // ==================================================================

    private static readonly ValueConverter<DateTimeOffset, long> DateTimeOffsetToLong = new(
        v => v.ToUniversalTime().ToUnixTimeMilliseconds(),
        v => DateTimeOffset.FromUnixTimeMilliseconds(v));

    private static readonly ValueConverter<DateTimeOffset?, long?> NullableDateTimeOffsetToLong = new(
        v => v.HasValue ? v.Value.ToUniversalTime().ToUnixTimeMilliseconds() : null,
        v => v.HasValue ? DateTimeOffset.FromUnixTimeMilliseconds(v.Value) : null);

    // DateOnly 同理，存成 20260927 这样的整数，既能比大小也勉强能看懂。
    private static readonly ValueConverter<DateOnly, int> DateOnlyToInt = new(
        v => v.Year * 10000 + v.Month * 100 + v.Day,
        v => new DateOnly(v / 10000, v / 100 % 100, v % 100));

    private static readonly ValueConverter<DateOnly?, int?> NullableDateOnlyToInt = new(
        v => v.HasValue ? v.Value.Year * 10000 + v.Value.Month * 100 + v.Value.Day : null,
        v => v.HasValue ? new DateOnly(v.Value / 10000, v.Value / 100 % 100, v.Value % 100) : null);

    /// <summary>
    /// 自动给所有实体的时间字段套上转换器。
    ///
    /// 【为什么不逐个字段写 HasConversion】
    /// 因为迟早会忘记某个字段。忘了之后的表现非常隐蔽：
    /// 那个字段能正常读写，但只要一用它做范围查询就崩溃。
    /// 这里统一遍历一遍模型，任何新增的时间字段都自动生效，不会漏。
    /// </summary>
    private static void ApplyTimeConverters(ModelBuilder modelBuilder)
    {
        foreach (var entityType in modelBuilder.Model.GetEntityTypes())
        {
            foreach (var property in entityType.GetProperties())
            {
                var clrType = property.ClrType;

                if (clrType == typeof(DateTimeOffset))
                {
                    property.SetValueConverter(DateTimeOffsetToLong);
                }
                else if (clrType == typeof(DateTimeOffset?))
                {
                    property.SetValueConverter(NullableDateTimeOffsetToLong);
                }
                else if (clrType == typeof(DateOnly))
                {
                    property.SetValueConverter(DateOnlyToInt);
                }
                else if (clrType == typeof(DateOnly?))
                {
                    property.SetValueConverter(NullableDateOnlyToInt);
                }
            }
        }
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
        b.Property(x => x.SkipReason).HasMaxLength(500);

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
        b.HasIndex(x => x.CreatedAt);          // 按创建时间排序 + 启动延迟统计区间查询

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

    private static void ConfigureActionLog(Microsoft.EntityFrameworkCore.Metadata.Builders.EntityTypeBuilder<ActionLog> b)
    {
        b.ToTable("action_logs");
        b.HasKey(x => x.Id);

        b.Property(x => x.TargetType).IsRequired().HasMaxLength(32);
        b.Property(x => x.Summary).IsRequired().HasMaxLength(500);

        // BeforeJson / AfterJson 是一整条记录的快照，可能较长，不限制长度。
        // SQLite 的 TEXT 本身不强制长度，这里不写 HasMaxLength 是有意的：
        // 被截断的快照会让撤销静默失效（写回去的记录缺字段），这比多占点空间糟得多。

        // ---------- 索引 ----------
        //
        // 【为什么按 BatchId 建索引】
        // 撤销的典型操作是"把这一批全撤了"，也就是按 BatchId 捞出若干条。
        // 没有索引的话，日志攒到几千条后每次撤销都要全表扫。
        b.HasIndex(x => x.BatchId);

        // 撤销列表的展示顺序：最近的在前。
        b.HasIndex(x => x.CreatedAt);

        // 冲突检测要找"这条记录最近被改过几次"，按目标查。
        b.HasIndex(x => new { x.TargetType, x.TargetId });

        // 【为什么这里不加软删除过滤】
        // 日志就是日志，它记录的是"发生过什么"，让它消失等于抹掉历史。
        // 不调用 HasQueryFilter，撤销记录会一直保留——
        // 这也是"永久可撤"（D4）能成立的前提：日志还在，就撤得回来。
    }

    private static void ConfigurePolicySetting(Microsoft.EntityFrameworkCore.Metadata.Builders.EntityTypeBuilder<PolicySetting> b)
    {
        b.ToTable("policy_settings");
        b.HasKey(x => x.Id);

        b.Property(x => x.Key).IsRequired().HasMaxLength(64);
        b.Property(x => x.Value).IsRequired().HasMaxLength(500);
        b.Property(x => x.DefaultValue).IsRequired().HasMaxLength(500);
        b.Property(x => x.Reason).HasMaxLength(1000);

        // 【为什么 Key 必须唯一】
        // 同一个参数存两行的话，读取时取到哪一行是不确定的，
        // 表现为"设置改了但没生效"——这类 bug 极难查，因为数据看起来是对的。
        // 数据库层面的唯一约束是唯一可靠的兜底（代码里先查再插会有竞态）。
        b.HasIndex(x => x.Key).IsUnique();

        b.HasQueryFilter(x => x.DeletedAt == null);
    }

    private static void ConfigureChatMessage(Microsoft.EntityFrameworkCore.Metadata.Builders.EntityTypeBuilder<ChatMessage> b)
    {
        b.ToTable("chat_messages");
        b.HasKey(x => x.Id);

        b.Property(x => x.Role).IsRequired().HasMaxLength(16);
        b.Property(x => x.Text).IsRequired();

        // ---------- 索引 ----------

        // 打开对话页要取"最近若干条"，按时间倒序扫。
        b.HasIndex(x => x.CreatedAt);

        // 撤回后要把那一批标记成已撤回。
        b.HasIndex(x => x.BatchId);

        // 【为什么这里不加 HasQueryFilter】
        // 和 action_log 一个道理：对话就是历史，删了就没了。
        // 真要清理由用户显式操作（清屏），那时才写 DeletedAt。
        // （HasQueryFilter 不加 = 查询默认不过滤软删，
        //   所以"清空"要自己带 DeletedAt == null 条件，见 ChatRepository。）
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

                    // CreatedAt 只在首次新建时写一次，之后永不更新。
                    // 判断 default 而不是无条件赋值，是为了给"同步过来的数据"留后路：
                    // 从服务端拉下来的记录带着它原本的创建时间，不能被本地接收时间覆盖掉，
                    // 否则跨设备一同步，所有历史任务的启动延迟数据就全废了。
                    if (entry.Entity.CreatedAt == default)
                    {
                        entry.Entity.CreatedAt = now;
                    }

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
