using System.Text.Json.Serialization;

namespace DayManager.Api.Models;

/// <summary>
/// 模型要求执行的一个动作。
///
/// 【为什么用这种"结构化动作指令"，而不是行业里更常见的 function calling】
/// function calling 是标准做法，但它要求模型供应商严格支持 tools 协议，
/// 而不同供应商的实现细节（tool_calls 的返回格式、并行调用、参数纠错）差异很大，
/// 一旦某次返回格式不合规，整轮对话就废了。
/// 这里走的是另一条路：让模型在 JSON 里**声明它想做什么**，由我们自己的代码执行。
/// 好处有三个：
///   1. 完全不依赖供应商的 tools 支持，换模型不用改代码；
///   2. 每个动作在执行前都能被我们校验（不存在的 id、非法状态、超出范围），
///      模型说错也不会把数据写坏；
///   3. 天然能记 action_log —— 执行前存 before、执行后存 after，撤销立刻可用。
/// 代价是模型有时会输出我们没定义的 op，那时的处理是**明确报错并告诉它**，
/// 而不是猜一个近似操作去执行。
/// </summary>
public sealed class AiAction
{
    /// <summary>
    /// 操作类型：create / set_status / reschedule / rename / delete / query。
    ///
    /// 【query 是唯一的只读动作】
    /// 其余五个都会改数据、进撤销批次；query 只把某段日子的清单查回来给它看，
    /// 不写库、不记 action_log。所以它**不经过 ItemTools**，由 AgentService 自己消化：
    /// 查完把结果回喂给模型，再让它出最终答复（两轮）。
    /// </summary>
    public string Op { get; set; } = string.Empty;

    /// <summary>目标记录的 Id。create 时不需要。</summary>
    public Guid? Id { get; set; }

    /// <summary>标题。create 与 rename 需要。</summary>
    public string? Title { get; set; }

    /// <summary>目标状态（英文枚举名）：pending / in_progress / done / skipped / abandoned。</summary>
    public string? Status { get; set; }

    /// <summary>
    /// 日期，yyyy-MM-dd。create 与 reschedule 里表示「安排/挪到哪一天做」；
    /// query 时是查询起始日。
    ///
    /// 【和 DueDate 的区别，别混】
    /// 只说「12-15 交报告」「下周三之前」、没说哪天动手 → 用 DueDate，
    /// 那是「只有截止日的事」：中间不属于任何一天，一直挂着但不算今天该做完。
    /// 说「明天下午写」「15 号那天做」→ 用 Date，那是把事安排在了具体一天。
    /// </summary>
    public string? Date { get; set; }

    /// <summary>
    /// 只有截止日，yyyy-MM-dd，可空。仅 create 使用。
    ///
    /// 【为什么不能复用 Date】
    /// 复用的话系统分不清「12-15 那天做」和「12-15 是最后期限」——
    /// 后者中间哪天做都行，一旦按 Date 建就会被当成那一天的事，
    /// 到期前整天不出现、AI 还容易顺手把它摊派到各天去拆。
    /// 单独一个字段，AI 才有一句明确的话能说「这条别定死在哪天」。
    /// </summary>
    public string? DueDate { get; set; }

    /// <summary>
    /// 查询结束日，yyyy-MM-dd，可空（不给就只查 date 这一天）。只用于 query。
    /// 一次最多 31 天，超出的由 DayQuery 裁掉并说明。
    /// </summary>
    public string? DateTo { get; set; }

    /// <summary>具体几点，HH:mm。可空，给了就表示"到点开始"。</summary>
    public string? Time { get; set; }

    /// <summary>原因。设为 skipped 时写进来（"今天有事"）。</summary>
    public string? Reason { get; set; }

    /// <summary>预计用时（分钟）。create 时可给。</summary>
    public int? EstimateMinutes { get; set; }

    /// <summary>
    /// 这条是不是阶段性目标（容器），可空。仅 create 使用。
    ///
    /// 【为什么单独一个字段，而不是让 AI 直接填枚举】
    /// ① 形态（定日/定段/定点）由 date / dueDate / time 二选一推导，代码说了算，
    /// 不让模型自己填枚举——它分不清「15 号做」和「15 号截止」，11-21 就栽在这。
    /// ② 容器没有这种歧义：一条要么是装别的事的壳，要么是一件能做完的事。
    /// 只把这一个布尔交给模型，其余分类仍由创建入口写死。
    ///
    /// 【可递归】阶段性目标本身也是一条记录，可以再挂到另一个阶段性目标下
    /// （用 parentTitle），所以「数学竞赛 → 第一章 → 做题」这样的三层能建出来。
    /// </summary>
    public bool? IsStage { get; set; }

    /// <summary>
    /// 父任务（阶段性目标）的标题，可空。仅 create 使用。
    /// 同一批 actions 里先建容器、再建子项，执行器按顺序跑，后一条能找到前一条。
    /// 找不到就明确报错，不猜一个相近的（执行器「不猜」原则）。
    /// </summary>
    public string? ParentTitle { get; set; }

    /// <summary>
    /// 是不是「每次各算一次」的事（每天背单词、每天跑 5km），可空。仅 create 使用。
    ///
    /// 跟 IsStage 同一条规矩：只给模型一个布尔，枚举由创建入口写死，不让它自己填。
    /// 它只是**声明**「每一次独立结算、漏做在那天封账、不滚到明天」；
    /// RRule 的真实展开还没做——**不会自动生成后面几天的实例**，
    /// 所以别在 reply 里许诺「已经排好一周」。
    ///
    /// 只适用于普通定日 / 定点待办；和 dueDate（定段）或 isStage（容器）同给会报错。
    /// </summary>
    public bool? RepeatsDaily { get; set; }

    /// <summary>
    /// 这条是不是「不判成败」的（0 点上床睡觉这类没有完成动作的约束），可空。仅 create 使用。
    ///
    /// **它不是另一类任务**，只是这条记录的一种待遇：不进完成率分母、不算今天该做完、
    /// 不滚欠账、不许被点名催，也不递「先做 10 分钟」。
    /// 之所以要个字段，是因为离线兜底和各条链路得能直接读到「别碰这条」，
    /// 而不是靠标题里有没有「睡」字去猜（11-21 那张「先睡 10 分钟」就栽在猜上）。
    ///
    /// 只适用于普通定日 / 定点待办；和 dueDate（定段）或 isStage（容器）同给会报错。
    /// </summary>
    public bool? NoVerdict { get; set; }

    /// <summary>
    /// 所属项目名，可空。
    ///
    /// 【为什么拆一件事时必须带上它】
    /// 大任务页的进度是"子任务完成比例"算出来的，子任务没有项目归属，
    /// 那个页面就永远是空的——用户拆了八条，却没地方看到"这件事做到哪了"。
    /// 同一件事拆出来的每条都要写**同一个项目名**，系统会把它们归到一起；
    /// 名字相同的项目会被复用，不会每拆一次多建一个。
    /// </summary>
    public string? Project { get; set; }

    /// <summary>
    /// 这个项目的目标完成日期，yyyy-MM-dd，可空。
    /// 只需在拆这件事的第一条上给一次，后面的给不给都一样。
    /// </summary>
    public string? ProjectTarget { get; set; }
}

/// <summary>
/// 模型返回的完整结果：要说的话 + 要做的事。
///
/// 【为什么 reply 和 actions 放在同一个 JSON 里】
/// 决策 B8 定的是"乐观更新"：AI 直接做，做完告诉用户。
/// 如果要求它先问再做，每改一条都要多一轮往返，等二三十秒再问一遍"可以吗"——
/// 那是在给逃避找借口的时间。
/// 所以让它一次把"我做了什么"和"我为什么这么改"说清楚，
/// 用户不同意就点撤销（action_log 已经记好了）。
/// </summary>
public sealed class AgentReply
{
    /// <summary>
    /// 模型的草稿纸：**不给用户看**，后端读完就丢。
    ///
    /// 【为什么要有这个字段】
    /// deepseek-flash 是推理模型，它本来会先把"他在问什么、我该怎么答"想一遍。
    /// 但输出被强制成 JSON 之后，那一段思考没有地方放，
    /// 它就把思考过程写进了 reply —— 实测库里出现过这样的回复：
    /// "他只是在打招呼，我上一条把他当成要启动任务了。要承认这一点，短，不啰嗦……
    ///  保持两三句。actions 为空。"
    /// 用户看到的是 AI 在自言自语，而且它会越学越像：
    /// 那条脏回复进了历史之后，模型照着自己的上一条继续这么写。
    ///
    /// 给一个明确的落点，它就不用往 reply 里塞了。这比事后清洗可靠——
    /// 判断"这句话是不是自言自语"永远是猜，而让它没理由自言自语不用猜。
    /// </summary>
    public string? Think { get; set; }

    /// <summary>给用户看的话。</summary>
    public string Reply { get; set; } = string.Empty;

    /// <summary>要执行的动作。可以为空（只是聊天，不改数据）。</summary>
    public List<AiAction> Actions { get; set; } = new();

    /// <summary>
    /// 你猜他下一句会说什么，界面上做成输入框上方的一排快捷回复。
    ///
    /// 【为什么是"他可能说的话"，而不是"我要问他的问题"】
    /// 原先这里叫 questions（AI 追问 + 它的猜测），渲染成回复下方的一组输入框。
    /// 实测的问题是：那一组输入框把 AI 的回复顶出了屏幕——
    /// 用户问完一句，想看它说什么，结果先看到三行要填的框。
    /// 追问的入口不该是表单，话说不该被控件挤走。
    ///
    /// 改成：缺的信息**直接在 reply 里问出来**（一句话，一个问号）；
    /// 这里只放"他看到这句话之后最可能回的那几句"，
    /// 点一下就发出去，省掉打字——对一个启动困难的人来说，
    /// "点一下"和"想一句完整的回答"是两种代价。
    /// </summary>
    public List<string>? Suggestions { get; set; }
}

/// <summary>
/// 一轮对话的完整结果。
///
/// 【为什么要把执行结果和批次号一起返回】
/// 客户端拿到回复后要能立刻显示两件事：
///   1. 改了哪几条（一条都没说清楚的话，用户会以为 AI 在吹牛）
///   2. 撤销入口（batchId 直接就是撤销按钮的参数）
/// 分开两次请求去拿，界面上就会出现"话先出来了、改动列表后到"的错位。
/// </summary>
public sealed class AgentResult
{
    public bool Success { get; set; }

    /// <summary>失败原因。成功时为空。</summary>
    public string? Error { get; set; }

    /// <summary>AI 说的话。</summary>
    public string Reply { get; set; } = string.Empty;

    /// <summary>实际执行的动作与结果。</summary>
    public List<ActionOutcome> Outcomes { get; set; } = new();

    /// <summary>这批改动的批次号。非空表示可撤销。</summary>
    public Guid? BatchId { get; set; }

    /// <summary>猜他下一句会说什么，界面上做成输入框上方的一排快捷回复。</summary>
    public List<string>? Suggestions { get; set; }

    /// <summary>有没有真的改动数据。</summary>
    public bool Changed => Outcomes.Count > 0;

    public static AgentResult Failed(string error) => new() { Success = false, Error = error };
}

/// <summary>一个动作的执行结果。执行失败也要说清楚，不能静默跳过。</summary>
public sealed class ActionOutcome
{
    [JsonPropertyName("op")]
    public string Op { get; set; } = string.Empty;

    [JsonPropertyName("ok")]
    public bool Ok { get; set; }

    /// <summary>一句话说明做了什么，或为什么没做。</summary>
    [JsonPropertyName("detail")]
    public string Detail { get; set; } = string.Empty;

    /// <summary>受影响的记录 Id（新建时是新的 Id）。</summary>
    [JsonPropertyName("id")]
    public Guid? Id { get; set; }

    /// <summary>标题，界面上用来显示"改了哪几条"。</summary>
    [JsonPropertyName("title")]
    public string? Title { get; set; }
}
