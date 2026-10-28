using System.Globalization;

namespace DayManager.App.Converters;

/// <summary>
/// 把 true 变 false、false 变 true。
///
/// 【为什么要写这个类，XAML 里没有现成的"非"吗】
/// 确实没有。MAUI 的绑定表达式不支持取反，
/// 所以"IsBusy 的时候按钮要禁用"这种最常见的需求只能靠转换器实现。
///
/// 【为什么 ConvertBack 也要实现】
/// IValueConverter 接口要求两个方法都在，哪怕只用得到 Convert。
/// 双向绑定（比如 Entry 的 Text）才会走到 ConvertBack，这里用不到，但必须写。
///
/// 【返回值为什么是 object 而不是 bool】
/// 接口签名定死了。MAUI 拿到返回值后自己转成目标属性的类型，
/// 直接返回 bool 就行，不需要（也不能）自己转型。
/// </summary>
public class InvertedBoolConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is bool b && !b;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is bool b && !b;
}
