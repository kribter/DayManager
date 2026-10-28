using System.Windows.Input;

namespace DayManager.App.Controls;

/// <summary>
/// 选目标日期的那块浮层（版式见 XAML 里的说明）。
///
/// 【这个控件为什么不做业务】
/// 它只负责"长什么样 + 把点击转出去"：日期选完了要存到哪儿、存完刷新哪张列表，
/// 那是页面自己的事（列表页要重查卡片、详情页要重算"还剩几天"）。
/// 如果它自己拿着仓储去写库，两个页面的调用方式就被绑死了，
/// 以后想把日期改成别的粒度（比如"这个月内"）就得同时改控件和两个页面。
///
/// 【BindableProperty 是干什么的】
/// 普通 C# 属性在 XAML 里绑不了（界面不知道它什么时候变了）。
/// 声明成 BindableProperty 之后，XAML 里的 {Binding} 才能读到它、写回它，
/// 而且"值变了 → 界面自动更新"这条链由框架负责，不用自己通知。
/// 代价是每个属性要写三行样板（静态字段 + CLR 包装），这是 MAUI 的固定套路。
/// </summary>
public partial class DatePickerSheet : ContentView
{
    public DatePickerSheet()
    {
        InitializeComponent();
    }

    /// <summary>面板标题：正在给哪个项目改日期。</summary>
    public static readonly BindableProperty TitleProperty = BindableProperty.Create(
        nameof(Title), typeof(string), typeof(DatePickerSheet), string.Empty);

    public string Title
    {
        get => (string)GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    /// <summary>
    /// 日历上当前选中的那天。双向：用户翻日历是它往页面写，页面打开面板时是它被页面写。
    /// </summary>
    public static readonly BindableProperty SelectedDateProperty = BindableProperty.Create(
        nameof(SelectedDate), typeof(DateTime), typeof(DatePickerSheet), DateTime.Today,
        BindingMode.TwoWay);

    public DateTime SelectedDate
    {
        get => (DateTime)GetValue(SelectedDateProperty);
        set => SetValue(SelectedDateProperty, value);
    }

    /// <summary>
    /// 日历能往前翻到哪。默认今天——目标日期选到过去没有意义。
    ///
    /// 【为什么默认值写成 DateTime.Today 而不是某个固定日期】
    /// BindableProperty.Create 是普通方法调用，参数在运行时求值，
    /// 所以这里可以写 DateTime.Today（它只是不能写进 attribute 里）。
    /// 求得的值在类型初始化时算一次，等于"程序启动那天"；
    /// 跨天开着的程序会停在昨天，代价是允许选到昨天——本来就没禁止过期日期，不值得为它加逻辑。
    /// </summary>
    public static readonly BindableProperty MinimumDateProperty = BindableProperty.Create(
        nameof(MinimumDate), typeof(DateTime), typeof(DatePickerSheet), DateTime.Today);

    public DateTime MinimumDate
    {
        get => (DateTime)GetValue(MinimumDateProperty);
        set => SetValue(MinimumDateProperty, value);
    }

    /// <summary>「就这天」。</summary>
    public static readonly BindableProperty ConfirmCommandProperty = BindableProperty.Create(
        nameof(ConfirmCommand), typeof(ICommand), typeof(DatePickerSheet));

    public ICommand? ConfirmCommand
    {
        get => (ICommand?)GetValue(ConfirmCommandProperty);
        set => SetValue(ConfirmCommandProperty, value);
    }

    /// <summary>「还没定」：把目标日期清空。</summary>
    public static readonly BindableProperty ClearCommandProperty = BindableProperty.Create(
        nameof(ClearCommand), typeof(ICommand), typeof(DatePickerSheet));

    public ICommand? ClearCommand
    {
        get => (ICommand?)GetValue(ClearCommandProperty);
        set => SetValue(ClearCommandProperty, value);
    }

    /// <summary>「取消」：什么都不改，收起面板。点遮罩走的也是这个。</summary>
    public static readonly BindableProperty CancelCommandProperty = BindableProperty.Create(
        nameof(CancelCommand), typeof(ICommand), typeof(DatePickerSheet));

    public ICommand? CancelCommand
    {
        get => (ICommand?)GetValue(CancelCommandProperty);
        set => SetValue(CancelCommandProperty, value);
    }

    /// <summary>快捷日期。参数是 "3d" / "1w" / "2w" / "1m"。</summary>
    public static readonly BindableProperty QuickPickCommandProperty = BindableProperty.Create(
        nameof(QuickPickCommand), typeof(ICommand), typeof(DatePickerSheet));

    public ICommand? QuickPickCommand
    {
        get => (ICommand?)GetValue(QuickPickCommandProperty);
        set => SetValue(QuickPickCommandProperty, value);
    }
}
