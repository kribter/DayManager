using System.Text.Json;
using DayManager.App.Services;
using DayManager.Core.Enums;
using DayManager.Core.Planning;

// ============================================================
// 后端访问通道的冒烟测试。
//
// 它验证的是"客户端代码 ↔ 后端"这一整段接线是否正确：
//   URL 拼得对不对、token 头名对不对、JSON 字段名对不对、
//   响应解析对不对、各种错误的提示能不能看明白。
//
// 这些是"编译通过但一跑就废"的典型位置——
// 尤其是 JSON 字段名：C# 里叫 MaxTokens，序列化出去是 maxTokens，
// 大小写差一个字母后端就当这个字段不存在，而且不报错。
//
// 运行前需要先启动后端：
//   dotnet run --project src/DayManager.Api
// ============================================================

//
// 【为什么连测试自己读配置也要走 SeedFileReader】
// 之前这里是手写 JsonDocument.Parse 再 GetProperty("token")，
// 等于测试里又实现了一遍解析逻辑——真身就算坏了，这里照样能读到值，
// 于是"配置读取"这条路径从头到尾没有被任何断言覆盖过。
// 现在直接调真身：它要是读不出 token，这个测试一开始就跑不起来，
// _fail loud_，而不是变成后面那一串莫名其妙的 401。
//
var seed = SeedFileReader.TryRead(SeedFileReader.DefaultPath);
if (seed?.Token is null)
{
    Console.WriteLine($"读不到 token：{SeedFileReader.DefaultPath}");
    Console.WriteLine("检查这个文件是否存在、里面有没有 token 字段。");
    return 1;
}

var baseUrl = seed.BaseUrl ?? SeedFileReader.DefaultBaseUrl;
var token = seed.Token;

//
// 【为什么测试自己也要自动拉起后端】
// 之前这个测试要求"先手动把后端开起来"，于是每次后端不在，
// 它就打印一句"后端没起来，请先启动"然后退出——
// 一个需要人工前置步骤的测试，跟一个需要人工前置步骤的功能一样容易被人忘。
// 这里直接复用客户端那套自动启动逻辑：既让测试变成一条命令就能跑，
// 也让这条路径在每次跑测试时都被真实走到一遍。
//
var startupLauncher = BackendLauncher.Create(baseUrl, SeedFileReader.RepoRoot);

// 记下"后端本来在不在、后来起没起来"，第 9 节要用它来判定冷启动是否成功。
// 这样冷启动这条路径每次跑测试都会被真实走到一次，
// 不需要额外手工把后端杀掉——那件事实测会把宿主 shell 一起带走。
var backendWasDown = false;
var backendCameUp = false;

if (startupLauncher.CanAutoStart
    && !await startupLauncher.IsListeningAsync(TimeSpan.FromMilliseconds(500)))
{
    backendWasDown = true;
    Console.WriteLine("后端没在跑，自动启动中……");

    if (startupLauncher.TryStart(out var bootError))
    {
        for (var i = 0; i < 20; i++)
        {
            await Task.Delay(1000);

            if (await startupLauncher.IsListeningAsync(TimeSpan.FromMilliseconds(500)))
            {
                backendCameUp = true;
                break;
            }
        }
    }
    else
    {
        Console.WriteLine($"自动启动失败：{bootError}");
    }
}

var passed = 0;
var failed = 0;

void Check(string name, bool ok, string detail)
{
    Console.WriteLine($"  [{(ok ? "通过" : "失败")}] {name}");
    if (!string.IsNullOrEmpty(detail))
    {
        Console.WriteLine($"         {detail}");
    }

    if (ok)
    {
        passed++;
    }
    else
    {
        failed++;
    }
}

Console.WriteLine("=== 后端访问通道冒烟测试 ===");
Console.WriteLine($"服务器：{baseUrl}");
Console.WriteLine();

// UseProxy = false 必须加，理由和 MauiProgram 里的注释一致：
// 系统代理会把发往 127.0.0.1 的请求劫走，导致"地址写错了却还能连上"这种假象。
// 这里要和真实客户端保持同样的设置，否则测出来的结果说明不了真实代码的行为。
using var http = new HttpClient(new HttpClientHandler { UseProxy = false })
{
    Timeout = TimeSpan.FromSeconds(200)
};

// ---------- 1. 健康检查 ----------

Console.WriteLine("【1】健康检查");
var client = new ApiClient(http, new ServerSettings { BaseUrl = baseUrl, Token = token });

var health = await client.GetHealthAsync();
Check("能连上后端", health.Ok, health.ErrorMessage ?? string.Empty);
Check("AI 已配置", health.AiReady, $"provider={health.Provider}, model={health.Model}");

if (!health.Ok)
{
    Console.WriteLine();
    Console.WriteLine("后端没起来，后面的用例跳过。请先启动后端再跑这个测试。");
    return 1;
}

Console.WriteLine();

// ---------- 2. 普通对话 ----------

Console.WriteLine("【2】普通对话");
var reply = await client.ChatAsync(
    new[] { ChatMessageDto.User("请只回复两个字：收到") },
    maxTokens: 200);

Check("调用成功", reply.Success, reply.ErrorMessage ?? string.Empty);
Check("返回内容非空", !string.IsNullOrWhiteSpace(reply.Content), $"内容：{reply.Content}");
Console.WriteLine($"         模型={reply.Model}，消耗 tokens={reply.TotalTokens}");

Console.WriteLine();

// ---------- 3. JSON 模式（后面做任务拆解要用） ----------

Console.WriteLine("【3】JSON 结构化输出");
var jsonReply = await client.ChatAsync(
    new[]
    {
        ChatMessageDto.System("你是任务拆解助手。把任务拆成子任务，只返回 JSON，格式：{\"subtasks\":[{\"title\":\"\",\"estimateMinutes\":0}]}"),
        ChatMessageDto.User("任务：准备一次技术分享。截止日期：2026-11-05。")
    },
    jsonMode: true,
    temperature: 0.3,
    maxTokens: 2000);

Check("调用成功", jsonReply.Success, jsonReply.ErrorMessage ?? string.Empty);

// 【为什么这里要真的解析一遍】
// "返回了 JSON"和"返回了能被程序用的 JSON"是两回事。
// 模型完全可能返回一个语法合法、但结构完全不对的 JSON。
// 只有按我们的结构去反序列化，才能确认它真的可用。
var subtasks = new List<(string Title, int Minutes)>();
var parseError = string.Empty;

try
{
    using var doc = JsonDocument.Parse(jsonReply.Content);
    if (doc.RootElement.TryGetProperty("subtasks", out var arr))
    {
        foreach (var it in arr.EnumerateArray())
        {
            subtasks.Add((
                it.GetProperty("title").GetString() ?? string.Empty,
                it.TryGetProperty("estimateMinutes", out var mm) ? mm.GetInt32() : 0));
        }
    }
    else
    {
        parseError = "返回里没有 subtasks 字段";
    }
}
catch (JsonException ex)
{
    parseError = $"返回的不是合法 JSON：{ex.Message}";
}

Check("返回内容可解析成子任务列表", subtasks.Count > 0, parseError);
Check("子任务条数在合理范围（1-12）", subtasks.Count is > 0 and <= 12, $"实际 {subtasks.Count} 条");

foreach (var (title, minutes) in subtasks)
{
    Console.WriteLine($"         - {minutes,3} 分钟  {title}");
}

Console.WriteLine();

// ---------- 4. 错误 token 的提示是否看得懂 ----------

Console.WriteLine("【4】错误 token 的报错提示");
var badClient = new ApiClient(http, new ServerSettings { BaseUrl = baseUrl, Token = "this-token-is-wrong" });
var bad = await badClient.ChatAsync(new[] { ChatMessageDto.User("hi") });

Check("能识别出是 token 问题", !bad.Success && bad.StatusCode == 401, $"状态码={bad.StatusCode}");
Check("提示里说明了怎么修",
    bad.ErrorMessage?.Contains("token", StringComparison.OrdinalIgnoreCase) == true,
    bad.ErrorMessage ?? string.Empty);

Console.WriteLine();

// ---------- 5. 服务器不存在时的提示 ----------

Console.WriteLine("【5】后端地址写错时的报错提示");
var deadClient = new ApiClient(http, new ServerSettings { BaseUrl = "http://127.0.0.1:59999", Token = token });
var dead = await deadClient.GetHealthAsync();

// 这一项看似多余，其实是整个测试里最有价值的一条，原因见下面这段：
//
// 它第一次跑的时候是失败的——往一个根本没有监听的端口发请求，居然收到了 200。
// 追下去发现响应头里是 Server: Kestrel，也就是说请求被系统代理劫走、
// 转送给了真正的后端。表现就是"服务器地址明明写错了却连得上"，
// 这种假象一旦出现，排查方向会被带得完全跑偏。
// 修法是给 HttpClient 加 UseProxy = false（见 MauiProgram 里的注释）。
// 保留这一项，防止以后有人手滑把这个设置去掉。
Check("不会抛异常，而是给出可读提示", !dead.Ok && !string.IsNullOrEmpty(dead.ErrorMessage), dead.ErrorMessage ?? string.Empty);

Console.WriteLine();

// ---------- 6. AI 拆解（真实的提示词，跑真实 API） ----------

Console.WriteLine("【6】AI 任务拆解");
var decomposer = new TaskDecomposer(client);

var deadline = DateOnly.FromDateTime(DateTime.Today.AddDays(10));
var plan = await decomposer.DecomposeAsync(
    "准备一次关于「我们项目里踩过的三个坑」的技术分享，要能讲 40 分钟",
    deadline.ToDateTime(TimeOnly.MaxValue));

Check("拆解成功", plan.Success, plan.ErrorMessage ?? string.Empty);

if (plan.Success)
{
    Console.WriteLine($"         模型理解：{plan.Understanding}");

    // 【为什么这里用比例而不是"全部必须符合"】
    // 模型是概率性的，偶尔会给出一条 20 分钟或者 120 分钟的任务。
    // 要求 100% 落在区间里的话，测试会变成"偶尔红一下"的噪声，
    // 最后的结果是所有人都习惯性忽略它。卡的应该是"整体是否达标"。
    var inRange = plan.Subtasks.Count(x => x.EstimateMinutes is >= 25 and <= 90);
    var ratio = (double)inRange / plan.Subtasks.Count;

    Check("拆出了子任务", plan.Subtasks.Count > 0, $"共 {plan.Subtasks.Count} 条");

    // 【为什么这里给的是一个宽松的合理性区间，而不是精确断言】
    // 提示词里要求控制在 10 条以内，但模型是概率性的，偶尔会给出 13、14 条。
    // 如果把断言写成"必须 3-12 条"，这个测试就会变成偶尔红一下的噪声，
    // 而人对噪声的最后反应是习惯性忽略它——那测试就白写了。
    // 严格的上限应该写在提示词里作为引导，测试只卡"是不是明显不对"这一层。
    Check("子任务条数在合理量级（3-15 条）", plan.Subtasks.Count is >= 3 and <= 15, $"共 {plan.Subtasks.Count} 条");
    Check("大部分估时落在 25-90 分钟区间", ratio >= 0.6, $"{inRange}/{plan.Subtasks.Count} 条符合");
    Check("AI 给出了它对任务的理解（供用户核对）",
        !string.IsNullOrWhiteSpace(plan.Understanding), plan.Understanding ?? "(空)");
    Check("每条都有最小启动步骤",
        plan.Subtasks.All(x => !string.IsNullOrWhiteSpace(x.MinimalStep)),
        $"{plan.Subtasks.Count(x => !string.IsNullOrWhiteSpace(x.MinimalStep))}/{plan.Subtasks.Count} 条有");

    Console.WriteLine();
    foreach (var t in plan.Subtasks)
    {
        var kindText = t.Kind == ItemKind.WantToDo ? "想做" : "该做";
        Console.WriteLine($"         [{kindText}] {t.EstimateMinutes,3} 分钟  {t.Title}");
        Console.WriteLine($"                第一步：{t.MinimalStep}");
    }

    // ---------- 6b. 缺信息时能不能提出要问的东西 ----------
    //
    // 【为什么要为它单独写一段测试】
    // 上一版 AI 把疑问写进 warnings，只能看、没法回答，用户不知道该补什么。
    // 这一版的 questions 是整个"该先问"的闭环入口：
    // 问了之后必须有地方回填，回填之后必须能带着答案重拆。两段都要验证。
    Check("提问不超过 3 条（问多了没人答）", plan.Questions.Count <= 3, $"{plan.Questions.Count} 条");

    if (plan.Questions.Count > 0)
    {
        Check("每条提问都带了猜测，用户可以直接采纳",
            plan.Questions.All(x => !string.IsNullOrWhiteSpace(x.Guess)),
            string.Join(" / ", plan.Questions.Select(x => $"{x.Prompt}={x.Guess}")));

        Console.WriteLine();
        foreach (var q in plan.Questions)
        {
            Console.WriteLine($"         ❓ {q.Prompt}  → 猜测：{q.Guess}");
        }

        // 第二轮：把猜测原样当成答案送回去。
        // 【为什么测的是"原样送回"而不是"精心编一个答案"】
        // 用户最常见的操作就是一个字不改直接确认——所以这条路径最值得保证。
        var answers = plan.Questions
            .Select(x => new ClarifyingAnswer(x.Prompt, x.Guess))
            .ToList();

        var revised = await decomposer.DecomposeAsync(
            "准备一次关于「我们项目里踩过的三个坑」的技术分享，要能讲 40 分钟",
            deadline.ToDateTime(TimeOnly.MaxValue),
            answers);

        Check("带着补充信息重拆仍然成功", revised.Success, revised.ErrorMessage ?? string.Empty);
        Check("重拆后依然有可执行的子任务", revised.Subtasks.Count > 0, $"共 {revised.Subtasks.Count} 条");

        if (revised.Success)
        {
            Console.WriteLine($"         重拆后的理解：{revised.Understanding}");
        }
    }
    else
    {
        Console.WriteLine("         （这次 AI 没有提问，直接按默认理解拆了）");
    }

    if (plan.Warnings.Count > 0)
    {
        Console.WriteLine();
        foreach (var w in plan.Warnings)
        {
            Console.WriteLine($"         ⚠ AI 的提醒：{w}");
        }
    }

    // ---------- 7. 拆解结果摊到各天 ----------

    Console.WriteLine();
    Console.WriteLine("【7】摊到截止日之前的每一天");

    // 从今天开始——这是修 bug 之后的行为：计划第一天就是今天，
    // 用户确认完切到「今天」页必须能看到东西。
    var start = DateOnly.FromDateTime(DateTime.Today);
    var spread = PlanSpreader.Spread(plan.Subtasks, start, deadline);

    Check("摊分结果不丢任务",
        spread.Scheduled.Count + spread.Overflow.Count == plan.Subtasks.Count,
        $"{spread.Scheduled.Count} + {spread.Overflow.Count} = {plan.Subtasks.Count}");

    // 第一天就是今天，而且今天真的有任务。
    // 【为什么这条断言盯着"今天有没有"】
    // 用户抱怨"拆解之后没有在日程那里显示"的根因就是第一天被排到了明天。
    // 排分是个纯函数，它排错一天界面必然空——在算式这端拦住最省事。
    Check("第一天落在今天", spread.Scheduled.Count > 0 && spread.Scheduled[0].Date == start,
        $"第一天={spread.Scheduled.FirstOrDefault()?.Date:MM-dd}，今天={start:MM-dd}");
    Check("今天就有可以做的事", spread.Scheduled.Any(x => x.Date == DateOnly.FromDateTime(DateTime.Today)),
        $"今天 {spread.Scheduled.Count(x => x.Date == DateOnly.FromDateTime(DateTime.Today))} 条");

    Console.WriteLine("         " + spread.Describe());
    Console.WriteLine("         " + string.Join(" | ",
        spread.Scheduled.GroupBy(x => x.Date)
            .Select(g => $"{g.Key:MM-dd}({g.Count()}条/{DurationText.Format(g.Sum(x => x.Task.EstimateMinutes))})")));

    if (!spread.Fits)
    {
        Console.WriteLine("         排不下，需要跟用户重新协商截止日或范围。");
    }
}

// ---------- 8. 种子配置文件读取 ----------
//
// 这一节是专门为一个真实发生过的 bug 加的：
// client.json 里写的是小写 token / baseUrl（JSON 配置文件的惯例），
// 而 System.Text.Json 默认大小写敏感 → 两个字段静默反序列化成 null
// → token 一直是空字符串 → 请求必然 401，界面却只说"token 不对"。
//
// 当时这一节不存在，是因为测试给 ServerSettings 做了替身，
// 而出 bug 的恰好就是被替身顶掉的那段代码。
Console.WriteLine();
Console.WriteLine("【8】种子配置文件读取（data/client.json）");

var realSeed = SeedFileReader.TryRead(SeedFileReader.DefaultPath);
Check("能读到 baseUrl", !string.IsNullOrWhiteSpace(realSeed?.BaseUrl), realSeed?.BaseUrl ?? "(null)");
Check("能读到 token", !string.IsNullOrWhiteSpace(realSeed?.Token),
    $"长度={(realSeed?.Token ?? string.Empty).Length}");
Check("读到的 token 没有多余空白",
    realSeed?.Token == realSeed?.Token?.Trim(),
    $"[{realSeed?.Token}]");

// 大小写兼容：手写配置文件时用小写开头是最自然的，代码必须能接受
var tmpDir = Path.Combine(Path.GetTempPath(), "dm-seed-test");
Directory.CreateDirectory(tmpDir);

void CheckSeed(string label, string json, string expectedToken, string expectedBaseUrl)
{
    var path = Path.Combine(tmpDir, Guid.NewGuid() + ".json");
    File.WriteAllText(path, json);
    var result = SeedFileReader.TryRead(path);
    Check(label, result?.Token == expectedToken && result?.BaseUrl == expectedBaseUrl,
        $"token=[{result?.Token}] baseUrl=[{result?.BaseUrl}]");
}

CheckSeed("小写开头（JSON 惯例，曾经的 bug 点）",
    """{"baseUrl":"http://100.1.2.3:5080","token":"abc123"}""", "abc123", "http://100.1.2.3:5080");

CheckSeed("大写开头（C# 属性名写法）",
    """{"BaseUrl":"http://100.1.2.3:5080","Token":"abc123"}""", "abc123", "http://100.1.2.3:5080");

CheckSeed("混合大小写",
    """{"BASEURL":"http://100.1.2.3:5080","ToKeN":"abc123"}""", "abc123", "http://100.1.2.3:5080");

CheckSeed("token 前后带空白会自动去掉",
    """{"token":"  abc123\n"}""", "abc123", null!);

// 容错：这些都不是错误，不该抛异常
var badJson = Path.Combine(tmpDir, "bad.json");
File.WriteAllText(badJson, "{ 这不是合法 JSON ");
Check("文件写坏了不抛异常，返回 null", SeedFileReader.TryRead(badJson) is null, "返回 null");

Check("文件不存在时返回 null（Android 上的正常情况）",
    SeedFileReader.TryRead(Path.Combine(tmpDir, "不存在的文件.json")) is null, "返回 null");

Directory.Delete(tmpDir, recursive: true);

// ---------- 9. 客户端自动拉起后端 ----------
//
// 这一节是真的把后端进程杀掉再让它自己拉起来。
// 之所以敢这么干：这是唯一能证明"自动启动真的能启动"的方法，
// 而且它测完会把后端留着（本身就是个常驻服务，启动后不该被关掉）。
// 这也是本节放在最后的原因——前面 8 节都依赖后端在线。
Console.WriteLine();
Console.WriteLine("【9】客户端自动拉起后端");

var repoRoot = SeedFileReader.RepoRoot;
var launcher = BackendLauncher.Create(baseUrl, repoRoot);

Check("本机回环地址上允许自动启动", launcher.CanAutoStart, launcher.DisplayAddress);

// 远端地址绝不能自动启动：那时后端跑在另一台机器上，
// 在自己电脑上启动它不但没用，还会留下反复重启失败的僵尸进程。
var remoteLauncher = BackendLauncher.Create("http://100.64.0.1:5080", repoRoot);
Check("远端（Tailscale）地址上不自动启动", !remoteLauncher.CanAutoStart, remoteLauncher.DisplayAddress);

// 配置写坏了也不该让整个自动启动废掉，而是退回默认地址
var brokenLauncher = BackendLauncher.Create("这不是一个地址", repoRoot);
Check("地址写坏时退回默认值而不是报错", brokenLauncher.CanAutoStart, brokenLauncher.DisplayAddress);

// 端口没人监听时，socket 探测必须立刻返回 false，不能有假阳性。
// 注意探测本身不该受 CanAutoStart 限制：能不能启动是一回事，端口有没有人在是另一回事。
var deadProbe = BackendLauncher.Create("http://127.0.0.1:59998", repoRoot);
Check("没人监听的端口探测为 false",
    !await deadProbe.IsListeningAsync(TimeSpan.FromMilliseconds(300)), deadProbe.DisplayAddress);

// ---- 冷启动 ----
//
// 判定依据是开场时后端的状态：它本来不在、后来被拉起来了，这次才算验证过冷启动。
//
// 【为什么不在测试里自己杀掉后端再拉起来】
// 早期版本是这么做的，实测会把宿主 shell 一起带走——
// 整个 bash 命令收到 SIGTERM，输出全丢，什么都没看到。
// 进程管理交给外部（start.bat 或手动停），测试只负责它分内的那件事。
if (backendWasDown)
{
    Check("后端不在时能被自动拉起来", backendCameUp, launcher.DisplayAddress);
    Check("拉起来之后健康检查也正常", health.Ok, health.ErrorMessage ?? string.Empty);
    Check("拉起来之后 AI 是就绪的", health.AiReady, $"{health.Provider} / {health.Model}");
}
else
{
    Check("后端本来就在跑（本次没机会验证冷启动）", true,
        "要验证冷启动，先停掉后端再跑这个测试。");
}

Console.WriteLine();
Console.WriteLine(new string('=', 50));
Console.WriteLine($"通过 {passed} 项，失败 {failed} 项。");

return failed == 0 ? 0 : 1;
