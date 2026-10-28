using Microsoft.Data.Sqlite;

namespace DayManager.Data;

/// <summary>
/// 打开数据库之前要做的设置。目前只有一件：切到 WAL 模式。
///
/// 【为什么必须开 WAL】
/// SQLite 默认是 rollback journal 模式，**读写互斥**：
/// 一个连接在写的时候，别的连接读不了（反过来也一样）。
/// 而本项目恰好是"一边频繁写、一边读很多"的组合——
///   采集器每 5 秒写一次使用时长；
///   时间页刷新要读一整天的数据，还要按小时摊一遍（一个不短的事务）。
/// 两边撞上就是 `SQLite Error 5: database is locked`。
/// 采集刚刚接上的时候这种撞车是偶发的（刷新很少），
/// 一旦时间页开始定时自动刷新，撞上就是家常便饭。
///
/// WAL（Write-Ahead Logging）让读者和写者互不阻塞，是 SQLite 处理并发的标准做法。
///
/// 【为什么只设一次就够】
/// `journal_mode` 是写在**数据库文件头**里的持久设置，
/// 不像 `busy_timeout` 那样只对当前连接有效。所以程序启动时设一次即可，
/// 之后每次连接自动生效——这也是为什么它放在启动流程里，而不是每次开连接都执行。
///
/// 【会不会多出文件】
/// 会多出 `daymanager.db-wal` 和 `-shm` 两个伴随文件，这是正常的。
/// 注意：这再一次说明"把数据库文件放进网盘同步目录"为什么行不通
/// （见需求文档：网盘按整个文件同步，两端同时改必然互相覆盖）。
/// </summary>
public static class SqliteBootstrap
{
    public static void EnableWal(string dbPath)
    {
        try
        {
            using var connection = new SqliteConnection($"Data Source={dbPath}");
            connection.Open();

            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA journal_mode=WAL;";

            // 返回值是新的模式名（"wal"）。不需要它，但 PRAGMA 必须执行才生效。
            _ = command.ExecuteScalar();
        }
        catch (SqliteException)
        {
            // 设不上就算了：最坏情况是回到默认的读写互斥，症状是偶尔撞锁，
            // 而不是打不开数据库。为它让程序起不来不值得。
        }
    }
}
