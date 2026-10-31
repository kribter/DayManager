using CommunityToolkit.Mvvm.Messaging;
using DayManager.App.Messages;
using Microsoft.Maui.Graphics;
using Microsoft.Maui.Graphics.Platform;
using Microsoft.Maui.Storage;

// IImage 在两个命名空间里都有（MAUI 控件层也有一个同名的），不写全名会编译不过。
// 这里要的是绘图那一层的：它能 Downsize、能被画到 canvas 上。
using GraphicsImage = Microsoft.Maui.Graphics.IImage;

namespace DayManager.App.Services;

/// <summary>
/// 谈话两边的头像：AI 那个，和你自己那个。两个槽，一套做法。
///
/// 【为什么是两份而不是一份】
/// 之前只有 AI 一侧有头像，理由是"两边都摆会变成两排圆点"。
/// 站不住的地方在于：那条理由只在两边都是空占位符的时候成立。
/// 你自己选了图之后，两个圆里各是一张真的图，"这是谁说的"反而更好认了——
/// 而且它顺便回答了"这到底是一场对话，还是我在对一个工具下命令"。
///
/// 【为什么两个槽分开存，而不是一张表】
/// 两张图各存在自己的槽里，互不知道对方。
/// 换自己头像的时候，AI 那张解码好的图还在手上，不会被带着重读一遍。
/// </summary>
public sealed class AvatarService
{
    /// <summary>AI 那一边。</summary>
    public AvatarSlot Ai { get; } = new("ai", DefaultAvatar.Calendar);

    /// <summary>你自己这一边。</summary>
    public AvatarSlot Me { get; } = new("me", DefaultAvatar.Person);
}

/// <summary>
/// 一个头像槽：一张自己选的图，外加"没选的时候画什么"。
///
/// 【为什么界面的两个头像逻辑完全一致，却不合成一个类】
/// 合成一个类就得带一个参数到处传（这是哪个槽），
/// 而画出出来的地方恰恰需要"这一支笔专画这一个"。
/// 两个实例各握各的图和各自的 preferences key，反而不用传参。
/// </summary>
public sealed class AvatarSlot
{
    private const int KeyPathLength = 64;

    /// <summary>压到多长边。见下面"为什么要压到 256"。</summary>
    public const int MaxSize = 256;

    private readonly string _keyPath;
    private readonly string _fileName;

    private GraphicsImage? _custom;
    private bool _loaded;

    /// <param name="name">文件与存储键的名字，两个槽全靠它分开。</param>
    /// <param name="fallback">没选图时画哪个默认图形。</param>
    public AvatarSlot(string name, DefaultAvatar fallback)
    {
        _keyPath = $"avatar.{name}.path";
        _fileName = $"avatar-{name}.png";
        Fallback = fallback;
    }

    /// <summary>没选图时画哪个默认图形。</summary>
    public DefaultAvatar Fallback { get; }

    /// <summary>头像文件在本机的落点。</summary>
    public string FilePath => Path.Combine(FileSystem.AppDataDirectory, _fileName);

    /// <summary>
    /// 有没有自定义头像。按"图还在不在"算，不按"存没存过"——
    /// 文件被手动删了，这里就回到没有的状态，而不是留一个画不出来的设置。
    /// </summary>
    public bool HasCustom => Load() is not null;

    /// <summary>
    /// 当前那张图。没有时为 null，画的人负责画 <see cref="Fallback"/>。
    ///
    /// 【为什么缓存这个对象，而不是每次画都读一遍文件】
    /// 一屏几十个气泡，每个重绘一次就解一次码，滚动时是在反复做同一件事。
    /// 图一共只有一张，读一次握在手里就行。
    /// </summary>
    public GraphicsImage? Custom => Load();

    private GraphicsImage? Load()
    {
        if (_loaded)
        {
            return _custom;
        }

        _loaded = true;

        var path = Preferences.Default.Get(_keyPath, string.Empty);
        if (string.IsNullOrWhiteSpace(path) || path.Length > KeyPathLength || !File.Exists(path))
        {
            return null;
        }

        try
        {
            using var stream = File.OpenRead(path);
            _custom = PlatformImage.FromStream(stream);
        }
        catch (Exception ex)
        {
            // 图坏了不是什么大事：读不出来就当没设过，走默认图形。
            // 记一笔是因为"图还在但读不出来"多半是压那一步出了岔子。
            CrashLogger.Write($"读取头像（{_fileName}）", ex);
            _custom = null;
        }

        return _custom;
    }

    /// <summary>
    /// 弹系统文件选择器，选一张图当头像。
    ///
    /// 【为什么返回 bool 而不是抛异常】
    /// 选不到图（他取消了）、图读不出来（格式怪）、写入失败（没权限），
    /// 这三种都不是程序坏了，是这个动作没做成。
    /// 返回值让界面自己决定要不要说一句；抛出去只会让调用处堆一堆 try。
    /// </summary>
    public async Task<bool> PickAsync()
    {
        try
        {
            var picked = await FilePicker.Default.PickAsync(new PickOptions
            {
                PickerTitle = "选一张图当头像",
                FileTypes = FilePickerFileType.Images,
            });

            if (picked is null)
            {
                return false;
            }

            using var input = await picked.OpenReadAsync();
            if (input is null)
            {
                return false;
            }

            var original = PlatformImage.FromStream(input);
            if (original is null)
            {
                return false;
            }

            // Downsize 传的是"最长边缩到多少"，比例它自己保持，
            // 所以压完不一定正方形——画的时候再居中裁。
            var small = original.Width > MaxSize || original.Height > MaxSize
                ? original.Downsize(MaxSize, disposeOriginal: true)
                : original;

            var dest = FilePath;
            var dir = Path.GetDirectoryName(dest);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            using (var output = File.Create(dest))
            {
                small.Save(output, ImageFormat.Png, 1);
            }

            _custom = small;
            _loaded = true;
            Preferences.Default.Set(_keyPath, dest);

            WeakReferenceMessenger.Default.Send(new AvatarChangedMessage());
            return true;
        }
        catch (Exception ex)
        {
            CrashLogger.Write("换头像", ex);
            return false;
        }
    }

    /// <summary>恢复成默认的那个图形。</summary>
    public void Clear()
    {
        var path = Preferences.Default.Get(_keyPath, string.Empty);

        if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
        {
            try
            {
                File.Delete(path);
            }
            catch (Exception ex)
            {
                CrashLogger.Write("删除头像", ex);
            }
        }

        Preferences.Default.Remove(_keyPath);
        _custom = null;
        _loaded = true;

        WeakReferenceMessenger.Default.Send(new AvatarChangedMessage());
    }
}

/// <summary>没选图时，那一侧画什么。</summary>
public enum DefaultAvatar
{
    /// <summary>日历格子，中间一格是亮的（今天）。为 AI 那侧用，和程序图标同语义。</summary>
    Calendar,

    /// <summary>一个人的剪影。为你自己那侧用：它是日历，你是人，一眼分得开。</summary>
    Person,
}
