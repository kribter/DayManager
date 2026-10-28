using System.Text.Json;
using DayManager.Api.Models;
using DayManager.Api.Options;
using DayManager.Api.Services;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

// ==================================================================
// DayManager 后端
//
// 它目前只做两件事：
//   1) 给客户端一个能探测的入口（健康检查）
//   2) 代理 AI 请求
//
// 【为什么 AI 请求不能让客户端直接发给供应商】
// 调用 AI 需要 API key。MAUI 打出来的安装包是可以被反编译的，
// 任何硬编码（甚至加密后硬编码）在客户端里的字符串，本质上等同于公开——
// 因为解密逻辑本身也在包里。key 泄露的后果不是"被限额"，
// 而是别人拿着你的额度刷，账单记在你头上。
// 所以唯一正确的做法是：key 只存在于服务端，客户端只跟自己的服务端说话。
//
// 【为什么不干脆用现成的"AI 中转服务"，而要自己写这个后端】
// 除了 key 安全，还有两个需求本来就需要一个中心节点：
//   1. 文档里定了"AI 的写操作要能撤销"（ai_action_log），
//      必须有个地方集中记录发生了什么；
//   2. 手机和电脑要同步，也需要一个中心节点。
// 三个需求指向同一个服务，那就只维护一个，而不是三个各搞一套。
//
// 【现在为什么先只写 AI 代理，不做同步】
// 因为同步的正确性依赖后面几轮的真实数据验证，
// 而 AI 代理是纯转发，逻辑简单、今天就能验证。
// 先把它跑通，密钥一到位就能立刻用起来。
// ==================================================================

var builder = WebApplication.CreateBuilder(args);

// appsettings.Local.json 放本机专属配置（token、AI key）。
// 它已经在 .gitignore 里被忽略，永远不会进仓库。
//
// 【optional 必须是 true】
// 新克隆仓库的人没有这个文件，如果写成必选，程序直接起不来。
// reloadOnChange 让改配置不用重启——调 AI 提示词的时候这一点非常省时间。
builder.Configuration.AddJsonFile("appsettings.Local.json", optional: true, reloadOnChange: true);

// 强类型配置：把配置的一节绑定成 C# 对象。
//
// 【为什么不直接到处写 Configuration["Ai:ApiKey"]】
// 字符串键名拼错了编译器不会报错，只会在运行时拿到 null，
// 然后带着一个空的 key 去请求上游，得到一句看不懂的 401。
// 绑定成对象之后，属性名写错是编译错误，而且有智能提示。
builder.Services.Configure<AuthOptions>(builder.Configuration.GetSection(AuthOptions.SectionName));
builder.Services.Configure<AiOptions>(builder.Configuration.GetSection(AiOptions.SectionName));

// 【为什么用 AddHttpClient 而不是自己 new HttpClient】
// 直接 new HttpClient 有两个经典陷阱：
//   1. 每次 new 一个都会占用一条连接，频繁创建会耗尽端口；
//   2. 做成单例虽然解决了第一个问题，但它不感知 DNS 变化，
//      服务器换了 IP 之后会永远连不上，必须重启进程。
// IHttpClientFactory 内部统一管理连接生命周期，两个问题一起解决。
builder.Services.AddHttpClient();

// ------------------------------------------------------------------
// 数据库
//
// 【为什么后端要直接连数据库】
// M4 之前，后端只代理 AI，真正写数据的是客户端。
// 这在"AI 只能拆任务"时够用，但放到"AI 要能管理所有内容"就不行了：
//   - AI 主动提醒（早晨计划、晚间总结）发生在客户端没打开的时候，
//     那一刻没有客户端可以替它写数据；
//   - 手机端也要用同一套能力，不能每台设备各写一遍执行逻辑。
// 所以后端从"AI 代理"升级成"AI + 数据接口"，客户端退化为显示层。
//
// 【为什么和客户端用同一个库文件】
// 现在两者都跑在这台机器上。共用一个库，AI 写完客户端立刻能看到，
// 不需要再走一遍同步。等手机端接入（走 Tailscale）时，
// 这里换成服务端自己的库 + 同步，接口不变。
// ------------------------------------------------------------------
var dbPath = ResolveDbPath(builder.Configuration);
builder.Logging.AddFilter("Microsoft.EntityFrameworkCore.Database.Command", LogLevel.Warning);

builder.Services.AddDbContextFactory<DayManager.Data.AppDbContext>(options =>
    options.UseSqlite($"Data Source={dbPath}"));

// Repository 内部通过工厂现造 DbContext，自己没有可变状态，做成单例最省。
builder.Services.AddSingleton<DayManager.Data.Repositories.ItemRepository>();
builder.Services.AddSingleton<DayManager.Data.Repositories.ActionLogRepository>();
builder.Services.AddSingleton<DayManager.Data.Repositories.UsageRepository>();
builder.Services.AddSingleton<DayManager.Data.Repositories.ChatRepository>();

// AI 主动开口：它要自己读今天排了什么、做了多少、娱乐多久、昨天聊过什么。
builder.Services.AddSingleton<ProactiveService>();

// AI 建任务时要能把它挂到项目下（否则大任务页永远是空的）。
builder.Services.AddSingleton<DayManager.Data.Repositories.ProjectRepository>();
builder.Services.AddSingleton<ItemTools>();
builder.Services.AddSingleton<AgentService>();

// 【为什么是 Singleton】
// AiProxy 自己不保存任何请求状态，它每次都从 IOptionsMonitor 现读配置。
// 没有状态的东西做成单例最省，也避免了不必要的反复创建。
builder.Services.AddSingleton<AiProxy>();

var app = builder.Build();

// 启动时把表结构升到最新。
//
// 【为什么后端也要迁移，不能只靠客户端】
// 后端现在自己读写数据库了。如果它先于客户端启动，而库还是旧结构，
// 读 action_logs 会直接抛"没有这个表"——而那时客户端还没起来，没人替它升。
// Migrate 只执行没跑过的迁移，重复启动不会动数据。
using (var scope = app.Services.CreateScope())
{
    var dbFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<DayManager.Data.AppDbContext>>();
    await using var db = await dbFactory.CreateDbContextAsync();
    await db.Database.MigrateAsync();
}

// ------------------------------------------------------------------
// 极简认证：固定 token
//
// 【为什么不用 JWT / 账号体系】
// 这是给一个人用的程序，不存在"另一个用户要登录"这件事。
// 需要防的只有一种情况：同一个网络里的别人扫到端口就能读你的日程。
// 一个固定 token 足够挡住它。上 JWT 意味着要处理签发、刷新、过期、吊销——
// 为了一个不存在的问题付出真实的复杂度。
//
// 【fail-closed 原则（这一段里最重要的一条）】
// 没配 token 时，所有需要认证的请求一律拒绝，而不是放行。
// 默认放行是最危险的默认值：你会以为它受保护，实际上谁都能进。
// 宁可让人在启动时看到一句"请先配置 Auth:Token"，
// 也不要让日程数据在无人察觉的情况下裸奔。
// ------------------------------------------------------------------
app.Use(async (context, next) =>
{
    // 健康检查放行：客户端要靠它自动探测"服务器在不在"。
    // 它只返回一个 ok 和时间戳，不含任何数据，没有泄露风险。
    if (context.Request.Path.StartsWithSegments("/api/health"))
    {
        await next();
        return;
    }

    var authOptions = context.RequestServices.GetRequiredService<IOptions<AuthOptions>>().Value;

    if (string.IsNullOrWhiteSpace(authOptions.Token))
    {
        context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
        await context.Response.WriteAsync("服务端未配置 Auth:Token，拒绝处理请求。", context.RequestAborted);
        return;
    }

    if (!context.Request.Headers.TryGetValue(AuthOptions.HeaderName, out var provided)
        || provided.Count != 1
        || !string.Equals(provided[0], authOptions.Token, StringComparison.Ordinal))
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        await context.Response.WriteAsync("token 无效", context.RequestAborted);
        return;
    }

    await next();
});

// ------------------------------------------------------------------
// 端点
// ------------------------------------------------------------------

// 【为什么这里必须注入 IOptionsMonitor 而不是 IOptions】
// IOptions<T> 的 .Value 会被缓存，读到的永远是"第一次访问时"的值，
// 配置文件后来改了它也不更新。而 AiProxy 用的是 IOptionsMonitor（会热更新），
// 于是就出现了"AI 明明能用了，健康检查却说没配"这种自相矛盾的状态。
// 教训：只要配置支持热更新，读取的每一处都必须用 IOptionsMonitor，不能有例外。
app.MapGet("/api/health", (IOptionsMonitor<AiOptions> aiOptions) =>
{
    var opt = aiOptions.CurrentValue;

    return Results.Ok(new
    {
        status = "ok",
        serverTime = DateTimeOffset.UtcNow,

        // 顺便告诉客户端 AI 是否已经配好。
        // 【为什么放在健康检查里】
        // 客户端启动时探测一次就能知道"AI 现在能不能用"，
        // 从而在界面上给出准确状态，而不是等用户真的去点 AI 才发现没配。
        aiReady = !string.IsNullOrWhiteSpace(opt.ApiKey),
        aiProvider = opt.Provider,
        aiModel = opt.Model
    });
});

app.MapPost("/api/ai/chat", async Task<IResult> (
    ChatRequest request,
    AiProxy proxy,
    CancellationToken cancellationToken) =>
{
    if (request.Messages is null || request.Messages.Count == 0)
    {
        return Results.BadRequest(new { error = "messages 不能为空" });
    }

    var result = await proxy.ChatAsync(request, cancellationToken);

    // 原样透传上游的状态码和内容类型。
    // 【为什么要透传状态码而不是统一转 500】
    // 上游的 400（请求格式错）、401（key 无效）、429（限额到了）
    // 含义完全不同，客户端需要根据它们给出不同的提示。
    // 统一成 500 之后，用户看到的就只有一句"服务器错误"，无从下手。
    return result.Success
        ? Results.Content(result.Body, "application/json")
        : Results.Content(result.Body, "application/json", statusCode: result.StatusCode);
});

// ------------------------------------------------------------------
// 会动手的 AI：说完话，顺便把该改的改了
// ------------------------------------------------------------------
app.MapPost("/api/ai/agent", async Task<IResult> (
    AgentChatRequest request,
    AgentService agent,
    CancellationToken cancellationToken) =>
{
    if (request.Messages is null || request.Messages.Count == 0)
    {
        return Results.BadRequest(new { error = "messages 不能为空" });
    }

    var result = await agent.ChatAsync(request.Messages, request.Today, cancellationToken);

    // 【为什么失败也返回 200】
    // 这一轮的失败（模型输出不合法、某个动作没执行成）大部分是可以重试的，
    // 而且 reply 里往往已经说了有意义的话。
    // 用 HTTP 错误码的话，客户端会把它当网络故障处理，
    // 用户看到的是"连不上服务器"，而不是真正的原因。
    // 真正的网络/认证问题早在前置环节就返回 4xx/5xx 了。
    return Results.Ok(result);
});

// ------------------------------------------------------------------
// 主动开口：没有人提问，AI 自己找话说
//
// 【为什么触发判断不在后端】
// 后端不知道客户端还活着没有，也没有通知能力（它是个控制台进程，
// 弹不出 toast）。所以"该不该说"由客户端判断，"说什么"才交给这里。
// 后端要做的只是把话说好——它需要的数据全在库里，自己查得到。
// ------------------------------------------------------------------
app.MapPost("/api/ai/proactive", async Task<IResult> (
    ProactiveRequest request,
    ProactiveService proactive,
    CancellationToken cancellationToken) =>
{
    if (!Enum.TryParse<DayManager.Core.Proactive.ProactiveKind>(
            request.Kind, ignoreCase: true, out var kind) || kind == DayManager.Core.Proactive.ProactiveKind.None)
    {
        return Results.BadRequest(new { error = $"kind 不认识：{request.Kind}" });
    }

    var today = request.Today ?? DateOnly.FromDateTime(DateTime.Now);
    var result = await proactive.SpeakAsync(kind, request.ItemId, today, cancellationToken);

    // 【为什么失败也返回 200】
    // 和 /api/ai/agent 同一个道理：说不出话是"这次没说"，不是网络故障。
    // 客户端拿到 ok=false 安静跳过就行，没必要弹一个错误给用户——
    // 他根本没主动请求过这句话，凭什么给他看一个报错。
    return Results.Ok(new { ok = result.Ok, title = result.Title, text = result.Text, error = result.Error });
});

// ------------------------------------------------------------------
// 撤销：AI 改错了，一键撤回来（决策 D4）
//
// 【为什么入口在接口层而不在对话里】
// 发现 AI 改错了的那一刻，最不想做的事就是再跟它聊一轮。
// ------------------------------------------------------------------
app.MapGet("/api/undo", async (DayManager.Data.Repositories.ActionLogRepository logs,
    CancellationToken ct) =>
{
    return Results.Ok(await logs.GetUndoableBatchesAsync(20, ct));
});

app.MapPost("/api/undo/{batchId:guid}", async (
    Guid batchId,
    bool? force,
    DayManager.Data.Repositories.ActionLogRepository logs,
    CancellationToken ct) =>
{
    var result = await logs.UndoBatchAsync(batchId, force ?? false, ct);
    return Results.Ok(result);
});

// ------------------------------------------------------------------
// 日程数据接口
//
// 一期只给客户端够用的最小集。它们的存在不是为了"做个 REST API"，
// 而是为了让手机端接入时不用重写一遍：接口在这里，谁都能调。
// ------------------------------------------------------------------
app.MapGet("/api/items", async (
    string? date,
    DayManager.Data.Repositories.ItemRepository items,
    CancellationToken ct) =>
{
    var day = DateTime.TryParse(date, out var parsed)
        ? DateOnly.FromDateTime(parsed)
        : DateOnly.FromDateTime(DateTime.Now);

    var list = await items.GetDayListAsync(day, ct);
    var stats = await items.GetDayStatsAsync(day, ct);

    return Results.Ok(new { date = day, items = list, stats });
});

app.MapPost("/api/items", async (
    CreateItemRequest request,
    DayManager.Data.Repositories.ItemRepository items,
    CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(request.Title))
    {
        return Results.BadRequest(new { error = "标题不能为空" });
    }

    var day = DateTime.TryParse(request.Date, out var parsed)
        ? DateOnly.FromDateTime(parsed)
        : DateOnly.FromDateTime(DateTime.Now);

    TimeSpan? time = TimeSpan.TryParse(request.Time, out var t) ? t : null;

    var item = await items.AddToDayAsync(request.Title.Trim(), day, time, ct);
    return Results.Ok(item);
});

app.MapPost("/api/items/{id:guid}/status", async (
    Guid id,
    SetStatusRequest request,
    DayManager.Data.Repositories.ItemRepository items,
    CancellationToken ct) =>
{
    var parsed = ParseStatus(request.Status);
    if (parsed is null)
    {
        return Results.BadRequest(new { error = $"状态「{request.Status}」不认识" });
    }

    var updated = await items.SetStatusAsync(id, parsed.Value, request.Reason, ct);
    return updated is null ? Results.NotFound(new { error = "找不到这条记录" }) : Results.Ok(updated);
});

app.MapDelete("/api/items/{id:guid}", async (
    Guid id,
    DayManager.Data.Repositories.ItemRepository items,
    CancellationToken ct) =>
{
    await items.SoftDeleteAsync(id, ct);
    return Results.Ok(new { ok = true });
});

app.Run();

// ------------------------------------------------------------------
// 本地函数
// ------------------------------------------------------------------

// 【为什么默认路径要"往上找仓库根"，而不是写死】
// 后端既可能从项目目录跑（dotnet run），也可能从编译输出目录跑，
// 两种情况下的相对路径含义不同。写死一个就有一半情况是错的。
// 往上找 slnx 是本机开发期最可靠的锚点。
static string ResolveDbPath(IConfiguration configuration)
{
    var configured = configuration["Data:DbPath"];
    if (!string.IsNullOrWhiteSpace(configured))
    {
        return configured;
    }

    var dir = new DirectoryInfo(AppContext.BaseDirectory);
    while (dir is not null)
    {
        if (dir.GetFiles("DayManager.slnx").Length > 0 || dir.GetFiles("*.sln").Length > 0)
        {
            return Path.Combine(dir.FullName, "data", "daymanager.db");
        }

        dir = dir.Parent;
    }

    // 找不到仓库根就退回一个固定位置，总比连不上好——
    // 而且这时请求会明确失败并说清路径，不会悄悄连到一个空库。
    return Path.Combine(AppContext.BaseDirectory, "daymanager.db");
}

static DayManager.Core.Enums.ItemStatus? ParseStatus(string? text) => text?.Trim().ToLowerInvariant() switch
{
    "pending" => DayManager.Core.Enums.ItemStatus.Pending,
    "in_progress" => DayManager.Core.Enums.ItemStatus.InProgress,
    "done" => DayManager.Core.Enums.ItemStatus.Done,
    "skipped" => DayManager.Core.Enums.ItemStatus.Skipped,
    "abandoned" => DayManager.Core.Enums.ItemStatus.Abandoned,
    _ => null
};
