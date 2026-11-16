namespace DayManager.App.Services;

/// <summary>
/// 后端地址与访问令牌。
///
/// 【为什么这两个值要能配置，而不是写死在代码里】
/// 后端将来会换地址：现在是本机 127.0.0.1，等你用 Tailscale 让手机也能连，
/// 地址就变成 100.x.x.x 那个虚拟局域网地址；哪天真的上了云服务器，又会再变一次。
/// 写死意味着每次换地址都要改代码重新编译打包。
///
/// 【为什么用 Preferences 存，而不是自己写个配置文件】
/// MAUI 的 Preferences 在各平台上都对应系统原生的轻量存储：
/// Windows 上是注册表，Android 上是 SharedPreferences。
/// 自己写文件要处理"存哪、权限、并发、格式"，而这些系统已经做好了。
/// </summary>
public sealed class ServerSettings
{
    private const string KeyBaseUrl = "server.baseUrl";
    private const string KeyToken = "server.token";

    /// <summary>
    /// 默认地址。后端现在就跑在本机 5080 端口上。
    /// 常量的位置见 <see cref="SeedFileReader.DefaultBaseUrl"/> 的说明。
    /// </summary>
    public const string DefaultBaseUrl = SeedFileReader.DefaultBaseUrl;

    public string BaseUrl { get; set; } = DefaultBaseUrl;

    public string Token { get; set; } = string.Empty;

    public static ServerSettings Load()
    {
        var settings = new ServerSettings
        {
            BaseUrl = Preferences.Default.Get(KeyBaseUrl, DefaultBaseUrl),
            Token = Preferences.Default.Get(KeyToken, string.Empty)
        };

        // 第一次运行时 Preferences 里什么都没有，这时尝试从本地种子文件读一次。
        //
        // 【为什么要这个种子文件，而不干脆把 token 写进代码】
        // 写进代码就等于提交进 git 仓库。这个 token 虽然只是你自己电脑上的东西，
        // 但一旦哪天仓库被推到远程或者分享出去，它就成了公开信息。
        // data/ 整个目录已经在 .gitignore 里，种子文件放这里既不用手动配置，也不会泄露。
        //
        // 【为什么只在 Windows 上读】
        // 手机端连的是另一台机器（你自己的电脑），不存在"本机上的后端配置文件"这回事，
        // 所以 Android 上只能靠 Preferences。
        if (string.IsNullOrWhiteSpace(settings.Token) && OperatingSystem.IsWindows())
        {
            settings.ApplySeedFile();
        }

        return settings;
    }

    public void Save()
    {
        Preferences.Default.Set(KeyBaseUrl, BaseUrl);
        Preferences.Default.Set(KeyToken, Token);
    }

    /// <summary>
    /// 从本地种子文件补上还没配置过的值。文件不存在就什么也不做——
    /// 这是正常情况，不是错误。
    ///
    /// 【为什么解析逻辑不写在这里】
    /// 见 <see cref="SeedFileReader"/> 的说明：写在这里的话测试够不着，
    /// 而它恰恰是"读不到就静默变成空 token"这种最容易出错的地方。
    /// </summary>
    private void ApplySeedFile()
    {
        // 先找 exe 旁边那份（打包发给别人时只有它），找不到再回本机仓库里的固定位置。
        var seed = SeedFileReader.TryRead(SeedFileReader.PortablePath)
                   ?? SeedFileReader.TryRead(SeedFileReader.DefaultPath);

        if (seed is null)
        {
            return;
        }

        if (seed.BaseUrl is not null)
        {
            BaseUrl = seed.BaseUrl;
        }

        if (seed.Token is not null)
        {
            Token = seed.Token;
        }
    }
}
