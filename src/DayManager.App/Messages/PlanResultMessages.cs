using CommunityToolkit.Mvvm.Messaging;

namespace DayManager.App.Messages;

/// <summary>
/// AI 已经排好了一份计划，可以把它显示出来了。
///
/// 【为什么用消息通知页面去开这个子页面，而不是让 ViewModel 自己 new 一个页面】
/// ViewModel 一旦直接创建页面，就等于它知道了"结果长什么样、用哪种方式出现"，
/// 这两种知识本该是属于界面的。改一处界面（比如想把结果做成抽屉而不是全屏）就得动业务逻辑。
/// 发一条消息则把两件事分开了：ViewModel 只说"结果好了"，
/// 要不要显示、显示成什么样，由负责显示的那一侧决定。
/// </summary>
public sealed record PlanReadyMessage;

/// <summary>
/// 这次拆解已经结束了（写进了日程，或者用户点了返回）。
///
/// 【为什么"关闭子页面"也要走消息】
/// 关页面的动作发生在业务逻辑之后，但 ViewModel 并不知道自己在不在页面上被显示——
/// 它可能同时被别的地方用着。让正在显示的那一页自己关自己，
/// 是最不会出错的一种分工：谁开的谁关。
/// </summary>
public sealed record PlanClosedMessage;
