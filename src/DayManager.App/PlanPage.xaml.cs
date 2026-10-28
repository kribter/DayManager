using CommunityToolkit.Mvvm.Messaging;
using DayManager.App.Messages;
using DayManager.App.ViewModels;

namespace DayManager.App;

/// <summary>
/// 拆解页面的后台代码。
///
/// 和 MainPage 一样，这里刻意保持得很薄：只有三件事——
/// 接住注入进来的 ViewModel、设为 BindingContext、页面出现时探一次服务器。
/// 所有业务逻辑都在 PlanViewModel 里，这样以后把它挪到别的界面形态
/// （比如做成一个对话框）时，ViewModel 可以原样复用。
/// </summary>
public partial class PlanPage : ContentPage
{
    private readonly PlanViewModel _viewModel;

    public PlanPage(PlanViewModel viewModel)
    {
        InitializeComponent();

        _viewModel = viewModel;
        BindingContext = _viewModel;

        // ViewModel 说"结果好了"，这一页负责把它弹出来。
        //
        // 【为什么开页面的动作放在这里，而不是 ViewModel 里】
        // "结果显示在哪儿、以什么形式出现"是界面的事：
        // 做成弹窗、全屏页、还是侧边抽屉，都不该影响拆解这段业务逻辑。
        // 放在这一层之后，将来换成别的形式只改这一个文件。
        WeakReferenceMessenger.Default.Register<PlanReadyMessage>(this, async (_, _) =>
        {
            if (Navigation.ModalStack.Count > 0)
            {
                return;
            }

            // 结果页和这一页共用同一个 ViewModel：它显示的就是这份计划本身，
            // 不需要再传递一遍数据。共用也让"返回后再回来"变成零成本——
            // 数据在内存里，两次看到的是同一份，不会因为往返就重算一遍。
            //
            // 【为什么这里要 try-catch】
            // 拆解本身要等二三十秒。如果路上一切都顺利、最后开页面这一步失败了，
            // 用户看到的是"等了半分钟什么都没发生"——这是最伤信任的一种失败方式。
            // 弹出失败至少让他知道结果其实已经算好了，回去点一次还能看到。
            try
            {
                await Navigation.PushModalAsync(new PlanResultPage(_viewModel));
            }
            catch (Exception ex)
            {
                await DisplayAlertAsync(
                    "结果已经排好了，但没能弹出窗口",
                    ex.Message + "&#10;请回到这一页点「回去看拆解结果」。",
                    "好");
            }
        });
    }

    /// <summary>
    /// 每次进入页面时先确认后端在不在。
    ///
    /// 【为什么这里只查服务器，不顺便做别的】
    /// 拆解是要等二三十秒的重操作，不能放在页面出现时自动跑——
    /// 用户还没输入任何东西，跑它没有任何意义，还白白消耗一次 AI 调用。
    /// 这里只做成本极低、但能立刻给出反馈的事：告诉用户"现在能不能用"。
    /// </summary>
    protected override async void OnAppearing()
    {
        base.OnAppearing();
        await _viewModel.CheckServerCommand.ExecuteAsync(null);
    }
}
