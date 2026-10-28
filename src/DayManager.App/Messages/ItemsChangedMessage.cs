using CommunityToolkit.Mvvm.Messaging;

namespace DayManager.App.Messages;

/// <summary>
/// 日程数据发生了变化，谁在显示今日清单就重新加载一下。
///
/// 【为什么需要这个，而不是让页面自己在显示时刷新】
/// 「拆解」页写完了任务，用户切回「今天」页。
/// 指望那边自己刷新的前提是它知道要刷新——
/// 而页面的 OnAppearing 在 Shell 里切 Tab 时并不保证触发，
/// 结果就是"我明明确认了，今日清单还是空的"。这正是第一版的实际表现。
///
/// 改成"写的人主动说一声"，依赖关系就明确了：
/// 谁改数据谁负责通知，而不是指望读的人猜到要去问。
///
/// 【为什么用 WeakReferenceMessenger 而不是把另一个 ViewModel 注入进来】
/// 注入进来会让两个页面互相持有引用，谁也离不开谁（循环依赖）。
/// 而消息是匿名的：写的人不知道有谁在听，听的人也不知道是谁发的。
/// Weak 的意思是"弱引用"——页面被回收时消息中心不会把它强留住造成内存泄漏。
/// 这个类型来自已经引用的 CommunityToolkit.Mvvm，不需要加新包。
/// </summary>
public sealed record ItemsChangedMessage;
