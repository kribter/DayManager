using DayManager.Core.Enums;
using DayManager.Data;
using Microsoft.EntityFrameworkCore;

// ============================================================
// 这个程序只做一件事：把 EF Core 生成的 SQL 打印出来。
//
// 为什么值得写它：
// "DbSet 不是容器，Where 会被翻译成 SQL" 这句话讲十遍，
// 不如亲眼看到那句 SQL 一眼。看完你就不会再把它当成 List 了。
//
// 【关键点：ToQueryString() 不会真的去查数据库】
// 它只做"翻译"这一步，把结果打印出来。所以这里连数据库文件都不需要建。
// ============================================================

// 用内存数据库搭一个 DbContext，纯粹为了拿到查询翻译能力
var options = new DbContextOptionsBuilder<AppDbContext>()
    .UseSqlite("Data Source=:memory:")
    .Options;

await using var db = new AppDbContext(options);

Console.WriteLine("【1】光写 db.Items，对应的是这样一句 SQL：");
Console.WriteLine();
Console.WriteLine(db.Items.ToQueryString());

Console.WriteLine();
Console.WriteLine(new string('=', 70));
Console.WriteLine("【2】加上 .Where(...) 之后，SQL 变成了这样：");
Console.WriteLine();

var dayStart = new DateTimeOffset(2026, 10, 4, 0, 0, 0, TimeSpan.FromHours(8));

IQueryable<DayManager.Core.Entities.Item> query = db.Items
    .Where(x => x.StartAt >= dayStart && x.Status != ItemStatus.Abandoned);

Console.WriteLine(query.ToQueryString());

Console.WriteLine();
Console.WriteLine(new string('=', 70));
Console.WriteLine("【3】再排序、再取前几条，SQL 继续往上加：");
Console.WriteLine();

var paged = db.Items
    .Where(x => x.Status != ItemStatus.Done)
    .OrderBy(x => x.StartAt)
    .Take(5);

Console.WriteLine(paged.ToQueryString());

Console.WriteLine();
Console.WriteLine(new string('=', 70));
Console.WriteLine("要记住的三件事：");
Console.WriteLine("  1. 上面所有代码都没有碰过数据库，只是在'攒条件'");
Console.WriteLine("  2. 真正执行要等 .ToListAsync() / .FirstOrDefaultAsync() 这类方法");
Console.WriteLine("  3. 翻译不了的条件会在这里炸（运行时，不是编译期）");
