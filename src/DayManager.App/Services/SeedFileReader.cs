using System.Text.Json;

namespace DayManager.App.Services;

/// <summary>
/// 读取本地种子配置文件（<c>data/client.json</c>），给客户端补上后端地址和 token。
///
/// 【为什么这段逻辑要单独成一个类，而不是留在 ServerSettings 里】
/// 因为它是**唯一一处可能被测试覆盖的环节，却恰恰曾是最早出 bug 的地方**。
///
/// 事情是这样的：ServerSettings 依赖 MAUI 的 Preferences（Windows 上存注册表、Android 上存
/// SharedPreferences），控制台测试项目编译不了它，于是测试里给整个 ServerSettings 做了个替身。
/// 结果就是——出 bug 的正好是被替身顶掉的那段代码，测试跑得再绿也什么都证明不了。
///
/// 拆出来之后，这个类不引用任何 MAUI 类型，测试项目可以用
/// <c>&lt;Compile Include&gt;</c> 直接编译真身（和 ApiClient.cs 一样的做法），
/// 断言就落在真正的代码上，而不是落在复制品上。
///
/// 【通用教训】替身（stub / mock）替换掉的那段代码，测试是看不见的。
/// 如果一段逻辑既重要又难手测，优先把它拆到不依赖外部环境的类里，
/// 而不是给它做个替身把测试"跑绿"。</summary>
public static class SeedFileReader
{
    /// <summary>
    /// 仓库根目录。整个项目只有这一处硬编码的路径，
    /// data\ 和后端项目路径都由它推导出来。
    ///
    /// 【为什么允许硬编码】给自用的程序，位置固定比灵活重要——不用记、不会找错，
    /// 而且路径一旦换地方，只要改这一个常量，不会漏掉某处。
    /// </summary>
    public const string RepoRoot = @"F:\DayManager";

    /// <summary>
    /// 种子文件的固定位置。和数据库放在同一个目录，理由同上。
    /// </summary>
    public static readonly string DefaultPath =
        Path.Combine(RepoRoot, "data", "client.json");

    /// <summary>
    /// 随包发布时放在 exe 旁边的那一份（<c>seed.json</c>）。
    ///
    /// 【为什么要有第二个位置】
    /// 把程序打包发给别人时，对方电脑上没有 F:\DayManager，
    /// 于是 DefaultPath 读不到 → token 是空 → 后端每一次请求都 401，
    /// 而界面只会说"连不上"，看不出真正原因是没读到口令。
    /// 便携版把这一份直接放在 exe 旁边，谁拿到都能用。
    ///
    /// 两个位置都存在时以便携版为准（打包产物里只有它）。
    /// </summary>
    public static readonly string PortablePath =
        Path.Combine(AppContext.BaseDirectory, "seed.json");

    /// <summary>
    /// 后端地址的默认值。
    ///
    /// 【为什么这个常量放在这里，而不是放在 ServerSettings 里】
    /// BackendLauncher（也是零 MAUI 依赖的）需要它，而 ServerSettings 依赖 MAUI，
    /// 引用它就等于把 BackendLauncher 也拖进 MAUI，测试又够不着了。
    /// 它描述的是"本机后端在哪"，属于本地配置的一部分，放在这个模块里正好。
    /// </summary>
    public const string DefaultBaseUrl = "http://127.0.0.1:5080";

    private static readonly JsonSerializerOptions Options = new()
    {
        //
        // 【为什么必须开 PropertyNameCaseInsensitive】
        // System.Text.Json 默认是**大小写敏感**的，而这个 seed 文件是给人手写的，
        // JSON 配置文件惯例用小写开头（baseUrl / token），C# 属性却是大写开头。
        // 不开这个开关，两个字段会静默反序列化成 null——
        // token 一直是空字符串，请求必然 401，界面只会说"token 不对"，
        // 完全看不出其实是**根本没读到**。这个坑真实发生过一次。
        //
        // 这里选择让代码迁就人（接受任意大小写），而不是要求手写文件改成 PascalCase：
        // 手写配置时写小写是很自然的，为此报错是在惩罚正常行为。
        //
        PropertyNameCaseInsensitive = true
    };

    /// <summary>
    /// 读取种子文件。
    /// </summary>
    /// <returns>
    /// 读到了就返回内容；文件不存在或内容不是合法 JSON 时返回 <c>null</c>。
    ///
    /// 【为什么这两种情况都不抛异常】
    /// 文件不存在是**正常情况**（Android 上就没有这个文件），
    /// 写坏了也只是"省得手填"的便利功能失效而已。
    /// 为了让便利功能失效而打断启动流程不值得，真正连不上时会在调用处给出明确提示。
    /// </returns>
    public static SeedValues? TryRead(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            using var stream = File.OpenRead(path);
            var seed = JsonSerializer.Deserialize<SeedFile>(stream, Options);

            if (seed is null)
            {
                return null;
            }

            // Trim 是刻意的：手动编辑时很容易在行尾留下空格或换行，
            // token 这种长串复制粘贴时前后多带一个空格肉眼完全看不出来，
            // 但送出去就是 401。
            return new SeedValues(
                string.IsNullOrWhiteSpace(seed.BaseUrl) ? null : seed.BaseUrl.Trim(),
                string.IsNullOrWhiteSpace(seed.Token) ? null : seed.Token.Trim());
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public sealed record SeedValues(string? BaseUrl, string? Token);

    private sealed class SeedFile
    {
        public string? BaseUrl { get; set; }
        public string? Token { get; set; }
    }
}
