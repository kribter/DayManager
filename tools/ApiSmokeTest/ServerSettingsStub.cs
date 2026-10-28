// ============================================================
// 这里定义了 App 里 ServerSettings 的一个替身。
//
// 【为什么需要替身】
// 真正的 ServerSettings 用了 MAUI 的 Preferences（Windows 上对应注册表，
// Android 上对应 SharedPreferences），那东西在控制台程序里不存在，
// 所以那份文件没法在这个测试项目里编译。
//
// 【为什么不干脆把 ApiClient 也复制过来测】
// 复制一份就等于有两份会各自演化的代码，
// 测试跑通了也只能证明"复制品没问题"，说明不了真实代码。
// 所以：ApiClient 用真身（通过 csproj 里的 Compile Include 引进来），
// 只有它依赖的这一个配置类用替身。替身只有两个属性，几乎没有出错的空间。
//
// 注意：必须声明在 DayManager.App.Services 命名空间下，
// 这样编译器看到 ApiClient 里的 ServerSettings 时用替身补上即可。
// ============================================================
namespace DayManager.App.Services;

public sealed class ServerSettings
{
    public string BaseUrl { get; set; } = "http://127.0.0.1:5080";
    public string Token { get; set; } = string.Empty;
}
