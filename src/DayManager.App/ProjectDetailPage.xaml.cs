using DayManager.App.ViewModels;

namespace DayManager.App;

/// <summary>
/// 大任务详情页的后台代码。
///
/// 【为什么这里不写加载逻辑】
/// 项目 id 是导航参数带过来的（?projectId=xxx），
/// 它到的时候页面可能还没构造完，也可能早就构造完了。
/// 把"id 一到就去查"写在 ViewModel 的属性变更回调里，
/// 两种时序都能覆盖；写在这里反而要处理"OnAppearing 时 id 还没到"的情况。
/// </summary>
// [QueryProperty] 把 URL 里的 ?projectId=xxx 接到页面的 ProjectId 属性上。
// 必须写在页面类上（ViewModel 上的写法不同版本行为不一致），
// 页面再把它转交给 ViewModel——这样"从 URL 取参数"这件事
// 只在这一行声明，不用去解析 Shell.Current.CurrentState.Location。
[QueryProperty(nameof(ProjectId), "projectId")]
public partial class ProjectDetailPage : ContentPage
{
    public ProjectDetailPage(ProjectDetailViewModel viewModel)
    {
        InitializeComponent();
        BindingContext = viewModel;
    }

    /// <summary>导航带过来的项目 id（字符串形式的 Guid）。</summary>
    public string ProjectId
    {
        set
        {
            if (BindingContext is ProjectDetailViewModel vm)
            {
                vm.ProjectIdText = value;
            }
        }
    }
}
