using DayManager.App.ViewModels;

namespace DayManager.App;

/// <summary>
/// 大任务列表页的后台代码。和日程页一样只做三件事。
///
/// 【为什么每次出现都要重新加载】
/// 从详情页返回时，子任务可能被打过勾，进度得跟着变。
/// 靠"返回时刷新"的约定不如无脑重查可靠——一个人用的数据量，
/// 多查一次的开销远小于"显示旧进度"带来的困惑。
/// </summary>
public partial class ProjectsPage : ContentPage
{
    private readonly ProjectsViewModel _viewModel;

    public ProjectsPage(ProjectsViewModel viewModel)
    {
        InitializeComponent();
        BindingContext = _viewModel = viewModel;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        await _viewModel.LoadCommand.ExecuteAsync(null);
    }
}
