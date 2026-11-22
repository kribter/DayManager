namespace DayManager.App.Messages;

/// <summary>
/// 对话页往列表**顶部**插一批更早的消息之前发出。
/// 页面收到后记下当前滚动位置和内容高度，供插完之后把视口锚回去。
///
/// 【为什么非要这一对消息】
/// 往 ScrollView 顶部塞内容，视口会跟着"往回跳"：原来正看着的那条被顶到屏幕下面去了。
/// 翻更早历史的正确手感是**看的位置不动，上面的内容变多**（微信就是这样）。
/// 高度要等布局排完才知道，所以拆成"开始/结束"两条，让页面自己量。
/// </summary>
public sealed record ChatHistoryPrependStarted;

/// <summary>插入完成。页面这时按新增的高度补偿滚动位置。</summary>
public sealed record ChatHistoryPrepended;
