namespace DayManager.App;

/// <summary>
/// 对话页的后台代码。刻意写得很薄：这里只处理"这一页自己的事"
/// ——注入 ViewModel、页面出现时探一次后端、新消息来时把列表滚到底——
/// 其余全在 ChatViewModel 里。
///
/// 【为什么后台代码要薄】
/// 写在 .xaml.cs 里的逻辑没法单独测试，改起来还容易和界面生命周期纠缠不清
/// （比如"页面还没显示完就去刷新数据"这类问题）。
/// 能放进 ViewModel 的就别留在这一层。
/// </summary>
public partial class ChatPage : ContentPage
{
    private readonly ViewModels.ChatViewModel _viewModel;
    private bool _serverChecked;

    /// <summary>
    /// 上一次滚动任务的取消令牌。
    ///
    /// 【为什么需要它】
    /// 每加一条消息都会触发一次滚动，而一次滚动要分三个时间点执行。
    /// 首次加载历史是一口气加几十条——不取消的话会攒出几十个滚动任务、
    /// 上百次滚动，页面会明显卡一下。只留最后一个就够了：
    /// 反正它们的目标都是"最后一条"，中间的都白做。
    /// </summary>
    private CancellationTokenSource? _scrollCts;

    public ChatPage(ViewModels.ChatViewModel viewModel)
    {
        InitializeComponent();

        BindingContext = _viewModel = viewModel;

        // 新消息来了就滚到底（实现在下面）。
        _viewModel.Messages.CollectionChanged += (_, _) => ScrollToLast();

        // 回车发送、Shift+回车换行。
        //
        // 【为什么写成 partial 方法，而不是在这里写 #if WINDOWS】
        // 这两个平台的做法毫无共同点：Windows 要挂 WinUI 的 PreviewKeyDown，
        // Android 要监听 Editor 的 ImeAction。塞进同一个文件只会变成一堆条件编译。
        // partial 方法在没有实现的平台上，调用会被编译器直接删掉——
        // 将来接 Android 时即使暂时没实现也不会编译失败，只是回车还不响应，
        // 比一堆 #if 少一个"改了条件忘了另一边"的出错机会。
        AttachEnterToSend();
    }

    /// <summary>
    /// 把回车接到发送上。实现见各平台目录（Windows 版在
    /// <c>Platforms/Windows/ChatPage.Windows.cs</c>）。
    /// </summary>
    partial void AttachEnterToSend();

    protected override async void OnAppearing()
    {
        base.OnAppearing();

        // 只探一次：切个 Tab 回来就再等一轮健康检查，既慢又没意义。
        if (_serverChecked)
        {
            return;
        }

        _serverChecked = true;
        await _viewModel.CheckServerCommand.ExecuteAsync(null);

        // 历史要在探活之后加载：万一加载失败，页面上的提示会盖在
        // "准备好了"这类状态后面，用户看到的最后一句话才是有用的那句。
        await _viewModel.LoadHistoryCommand.ExecuteAsync(null);
    }

    private void ScrollToLast()
    {
        if (_viewModel.Messages.Count == 0)
        {
            return;
        }

        _scrollCts?.Cancel();
        _scrollCts = new CancellationTokenSource();
        var token = _scrollCts.Token;

        MainThread.BeginInvokeOnMainThread(async () =>
        {
            // 【为什么要滚三次】
            // 内容高度是**文字排版之后**才涨上来的。第一次消息刚进集合时，
            // MessageStack.Height 还是旧值，照它滚等于没滚；
            // 三个时间点覆盖了"短回复一下就成"和"长回复要排一会儿"两种情况。
            //
            // 这里和"滚到某一条"有个本质区别：**多滚一次是无害的**。
            // 目标位置每次都重新算，重复执行只是把同一个动作再做一遍；
            // 而"滚到某项"的每次结果取决于当时的布局，做多了反而会越滚越偏。
            foreach (var wait in new[] { 30, 120, 320 })
            {
                try
                {
                    await Task.Delay(wait, token);
                }
                catch (OperationCanceledException)
                {
                    // 已经有更新的滚动任务接手了，这次不用管了。
                    return;
                }

                var height = MessageStack.Height;

                // 还没量过（页面刚出来），这一趟先算了，等下一次。
                if (height <= 0)
                {
                    continue;
                }

                // 给的是**具体位置**而不是"某一项"：滚到内容的总高度，
                // ScrollView 会自己夹到能滚的最远处，也就是最底下。
                // 这个动作跟"气泡现在多高"无关，所以排完版再滚也是同一个结果。
                await MessageScroll.ScrollToAsync(0, height, false);
            }
        });
    }
}
