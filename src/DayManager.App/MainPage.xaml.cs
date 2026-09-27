using DayManager.App.ViewModels;

namespace DayManager.App;

/// <summary>
/// 页面的"后台代码"。
///
/// 【注意这一版相比上一版变薄了多少】
/// 上一版里这里有查数据库、拼字符串、处理点击的一堆逻辑；
/// 现在只剩下三件事：接收注入进来的 ViewModel、把它设为 BindingContext、页面出现时触发一次加载。
/// 这就是 MVVM 想要的效果：界面代码只管"界面这件事"，业务逻辑都住在 ViewModel 里。
///
/// 【为什么这样写代码更好】
/// 一个判断标准是：这个文件里有没有一句业务逻辑？
/// 理想状态是一句都没有。将来要把Windows 端换成别的界面、或者做一个"只看今天"的精简版，
/// ViewModel 可以直接拿来复用，因为它完全不依赖任何控件。
/// </summary>
public partial class MainPage : ContentPage
{
    private readonly MainViewModel _viewModel;

    public MainPage(MainViewModel viewModel)
    {
        InitializeComponent();

        // BindingContext 是数据绑定的"源头"。
        // 设置了它之后，XAML 里所有 {Binding xxx} 都会到这个对象上找属性。
        _viewModel = viewModel;
        BindingContext = _viewModel;
    }

    /// <summary>
    /// 页面每次显示时调用（包括第一次打开，以及将来从其他页面返回时）。
    ///
    /// 【为什么在这里加载数据，而不是在构造函数里】
    /// 构造函数执行时界面还没显示，此时做异步操作容易引发时序问题；
    /// 更重要的是：你在手机端改了日程后返回这个页面，需要看到最新的数据，
    /// 放在 OnAppearing 里就能保证每次出现都刷新。
    ///
    /// 【为什么是 async void】
    /// 重写父类方法必须保持签名一致，而 void 方法没法 await。
    /// 这是 async void 少数几个正当使用场景之一（另一个是事件处理器）。
    /// 代价是异常不会被自动捕获，所以 LoadAsync 内部必须自己 try-catch，它确实也那么做了。
    /// </summary>
    protected override async void OnAppearing()
    {
        base.OnAppearing();
        await _viewModel.LoadCommand.ExecuteAsync(null);
    }
}
