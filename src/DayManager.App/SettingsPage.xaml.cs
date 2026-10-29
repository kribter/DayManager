using DayManager.App.ViewModels;

namespace DayManager.App;

/// <summary>
/// 设置页的后台代码。
///
/// 【为什么这里只做两件事】
/// 这一页没有图表，也没有需要手动重画的东西——
/// 剩下唯一"XAML 绑不了"的就是 Entry 的失焦存盘，
/// 因为"打完一句话"这个时机只有控件自己知道。
/// </summary>
public partial class SettingsPage : ContentPage
{
    private readonly SettingsViewModel _viewModel;

    public SettingsPage(SettingsViewModel viewModel)
    {
        InitializeComponent();

        BindingContext = _viewModel = viewModel;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();

        await _viewModel.LoadCommand.ExecuteAsync(null);
    }

    /// <summary>
    /// 人设输入框失焦时存一次。
    ///
    /// 【为什么挂在失焦而不是 TextChanged】
    /// 每敲一个字写一次库，一行设置会变成几十行垃圾；
    /// 而失焦正好是"人打完了"的时刻。代价是中途切走会丢最后几个字——
    /// 一句话的人设，这个代价可以接受。
    /// </summary>
    private void OnPersonaUnfocused(object? sender, FocusEventArgs e)
    {
        if (_viewModel.SavePersonaCommand.CanExecute(null))
        {
            _viewModel.SavePersonaCommand.Execute(null);
        }
    }

    /// <summary>
    /// 密钥那几个输入框失焦即存。
    ///
    /// 【为什么不用按钮才存】
    /// 三个输入框共用一个统一的时机比让人记住"要点保存"简单；
    /// 而它们是空的操作不需要网络，落一次注册表没有副作用。
    /// 真正的验证留给"保存并试一下"那个按钮——那是要花钱的。
    /// </summary>
    private void OnAiKeyUnfocused(object? sender, FocusEventArgs e)
    {
        if (_viewModel.SaveAiCommand.CanExecute(null))
        {
            _viewModel.SaveAiCommand.Execute(null);
        }
    }
}
