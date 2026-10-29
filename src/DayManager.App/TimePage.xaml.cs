using System.ComponentModel;
using DayManager.App.Drawables;
using DayManager.App.ViewModels;

namespace DayManager.App;

/// <summary>
/// 时间页的后台代码。
///
/// 【为什么柱状图要在这里接一下】
/// GraphicsView 不是"绑定数据就会自己重画"的控件：它只知道"有人让我画"，
/// 不知道 ViewModel 里的数据变了。所以要么在 VM 里持有控件（那会让 VM 依赖界面），
/// 要么在这里监听数据变化再通知控件重画。后者是对的。
///
/// 【为什么每次 OnAppearing 都重新加载】
/// 采集是后台一直在跑的，切回来数字就该是新的。
/// 靠"什么时候会变"的约定去猜，不如无脑重查——一个人用的数据量，多查一次不值得省。
/// OnAppearing 不保证触发，所以真正的主力是 TimeViewModel 里的定时自动刷新。
/// </summary>
public partial class TimePage : ContentPage
{
    private readonly TimeViewModel _viewModel;
    private readonly UsageBarsDrawable _chart = new();

    public TimePage(TimeViewModel viewModel)
    {
        InitializeComponent();

        BindingContext = _viewModel = viewModel;

        Chart.Drawable = _chart;

        _viewModel.PropertyChanged += OnViewModelPropertyChanged;

        // 从这一刻起自己定时刷新（幂等）。放在构造函数里而不是 OnAppearing 里，
        // 是因为 Shell 切 Tab 不保证触发 OnAppearing——见 TimeViewModel.StartAutoRefresh 的说明。
        _viewModel.StartAutoRefresh();
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();

        // 回到这一页就立刻拿一次新的（能触发的话最好，触发不了还有定时兜底）。
        await _viewModel.LoadCommand.ExecuteAsync(null);
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(TimeViewModel.Bars))
        {
            return;
        }

        _chart.Bars = _viewModel.Bars;
        Chart.Invalidate();
    }
}
