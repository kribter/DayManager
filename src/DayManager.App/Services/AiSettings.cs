namespace DayManager.App.Services;

/// <summary>
/// 使用者的 AI 密钥（以及可选的接口地址、模型）。
///
/// 【为什么要有这个东西】
/// 这个软件是要能发给别人用的。密钥如果只能写进服务端的配置文件，
/// 分发出去的包里就得带着我自己那把——别人拿来直接烧我的额度。
/// 让每个人在自己的界面里填自己的密钥，随每次请求带给本机后端，
/// 服务端用完即弃、不写盘，才是"发出去但不送凭据"的唯一做法。
///
/// 【为什么存 Preferences 而不是 data/ 下的配置文件】
/// Preferences 在 Windows 上落在注册表（用户目录），Android 上是 SharedPreferences，
/// 都不在安装目录里。这一点是刚性的：一旦把密钥写进 <c>data/</c> 或
/// <c>appsettings.Local.json</c> 这类随程序目录一起走的文件，
/// "打包发给朋友"这个动作就等于把密钥复制了一份给他。
///
/// 【为什么不加密】
/// 加密只是把问题转移：密钥环的管理员还是本机上的这个程序，
/// 而能读到注册表的人本来就能读到进程内存。Windows 上它受用户会话保护——
/// 同一台机器上的另一个账户读不到。这个软件是单人自用，这个强度是够的。
/// </summary>
public sealed class AiSettings
{
    private const string KeyApiKey = "ai.apiKey";
    private const string KeyBaseUrl = "ai.baseUrl";
    private const string KeyModel = "ai.model";
    private const string KeySearchKey = "ai.searchKey";

    /// <summary>
    /// 默认接口地址。后端 ApplyAiDefaults 会给留空的请求兜底，
    /// 这里给出一份只是为了让重试。
    /// </summary>
    public const string DefaultBaseUrl = "https://api.deepseek.com";

    public const string DefaultModel = "deepseek-chat";

    public string ApiKey { get; set; } = string.Empty;

    public string BaseUrl { get; set; } = DefaultBaseUrl;

    public string Model { get; set; } = DefaultModel;

    /// <summary>
    /// 联网搜索的密钥（博查 / Tavily，跟聊天是两家不同的服务）。
    ///
    /// 【为什么它跟着这里一起存，而不是放服务端配置】
    /// 理由和聊天密钥一模一样：程序目录里的任何一行密钥都会在"打包发给别人"时跟着走。
    /// 它留空是完全正常的用法——AI 只是不能上网查，拆大事时照自己知道的来，
    /// 并且会在 reply 里说明"这部分可能不全"。所以它**不该拦着任何人用软件**。
    /// </summary>
    public string SearchApiKey { get; set; } = string.Empty;

    /// <summary>填了密钥才算配好。没配时界面要明确指路，不能让人对着一个转圈的对话框猜。</summary>
    public bool HasKey => !string.IsNullOrWhiteSpace(ApiKey);

    /// <summary>填了才能上网查；没填不报错，只是搜索结果会是一句"没配 key"。</summary>
    public bool HasSearchKey => !string.IsNullOrWhiteSpace(SearchApiKey);

    public static AiSettings Load() => new()
    {
        ApiKey = Preferences.Default.Get(KeyApiKey, string.Empty),
        BaseUrl = Preferences.Default.Get(KeyBaseUrl, DefaultBaseUrl),
        Model = Preferences.Default.Get(KeyModel, DefaultModel),
        SearchApiKey = Preferences.Default.Get(KeySearchKey, string.Empty)
    };

    public void Save()
    {
        Preferences.Default.Set(KeyApiKey, ApiKey);
        Preferences.Default.Set(KeyBaseUrl, BaseUrl);
        Preferences.Default.Set(KeyModel, Model);
        Preferences.Default.Set(KeySearchKey, SearchApiKey);
    }

    /// <summary>
    /// 界面上显示用：只留头尾，中间打码。
    ///
    /// 【为什么不在日志或任何持久化输出里用这个方法】
    /// 它是给眼睛看的。任何"方便排查"的打印一旦带上密钥原文，
    /// 日志就成了新的泄露点——这个软件已经把"不打密钥"定成硬规矩，别破例。
    /// </summary>
    public static string Mask(string? key)
    {
        if (string.IsNullOrWhiteSpace(key) || key.Length <= 8)
        {
            return string.Empty;
        }

        return key[..6] + "…" + key[^4..];
    }
}
