using CommunityToolkit.Mvvm.Messaging;
using DayManager.App.Messages;
using DayManager.App.ViewModels;

namespace DayManager.App;

/// <summary>
/// 拆解结果的专属页面。
///
/// 和另外两个页面一样刻意写得很薄：这里只处理"这一页自己的生命周期"
/// ——它是怎么被打开的、什么时候该关掉自己——其余全在 PlanViewModel 里。
/// 打开它的动作在 PlanPage.xaml.cs 里（那边收到"结果好了"的消息把这一页弹出来）。
/// </summary>
public partial class PlanResultPage : ContentPage
{
    private bool _isClosing;

    public PlanResultPage(PlanViewModel viewModel)
    {
        InitializeComponent();

        BindingContext = viewModel;

        // 关页面的指令来自 ViewModel（写进日程成功了、或者用户点了返回），
        // 由这一页自己执行关闭。
        //
        // 【为什么不让 ViewModel 直接 Navigation.PopModalAsync】
        // ViewModel 不知道、也不该知道自己是不是正被显示；
        // 让它去操作导航栈，等于把"界面怎么组织"的知识塞进业务逻辑里。
        // 谁处在导航栈里谁知道怎么退出自己，所以由这一页来收消息、自己关。
        WeakReferenceMessenger.Default.Register<PlanClosedMessage>(this, OnClosed);
    }

    private async void OnClosed(object? recipient, PlanClosedMessage message)
    {
        // 保护：同一条消息被收到两次时（比如导航动画期间又来一条），
        // 第二次 Pop 会试图关掉已经不在栈上的页面，MAUI 会抛异常并表现为闪退。
        if (_isClosing)
        {
            return;
        }

        _isClosing = true;

        try
        {
            // 【为什么要分三种情况试】
            // 出这一页时用的是谁推的导航栈，取决于它当时是从哪儿弹出来的
            // （从 Shell 的某个 Tab 页弹出，还是从别的地方弹出），各处拿到的 Navigation 代理不完全一致。
            // 按"自己 → Shell → 当前窗口"的顺序找一个确实关得掉的去关，
            // 比赌某一个一定成立要稳。三种都不成立说明它本来就没显示，什么都不用做。
            if (Navigation.ModalStack.Count > 0)
            {
                await Navigation.PopModalAsync();
            }
            else if (Shell.Current?.Navigation.ModalStack.Count > 0)
            {
                await Shell.Current.Navigation.PopModalAsync();
            }
            else if (Navigation.NavigationStack.Count > 1)
            {
                await Navigation.PopAsync();
            }
        }
        catch (Exception)
        {
            // 关闭失败最坏的结果是"页面还开着"，用户点一下返回照样能走，
            // 不值得为它弹一个错误框——那才是把小故障变成烦人的事。
        }
    }

    protected override void OnDisappearing()
    {
        // 不再显示了就别再听这条消息。
        // 不注销的话，下一次打开结果页时新旧两页会同时收到关闭消息，
        // 旧的那一页早已不在栈上，尝试关闭时就会出错。
        WeakReferenceMessenger.Default.Unregister<PlanClosedMessage>(this);
        base.OnDisappearing();
    }
}
