using DayManager.Core.Entities;
using DayManager.Core.Enums;

namespace DayManager.Core.Planning;

/// <summary>
/// 一条记录在界面上该怎么称呼它自己。
///
/// 【为什么这个也放 Core，而不是写在 ItemViewModel 里】
/// 它回答的是「这条是什么形态」，和 ItemAxes 里那些判据是同一个问题的两种问法：
/// 判据给代码用（进不进清单、算不算分母），这里给人看（清单开头写哪两个字）。
/// 放 Core 是为了**只写一份**——界面、将来 AI 提示词、冒烟都读它，
/// 免得界面写「每天」、别处写「每日」，同一件事出现两个说法。
/// 附带的好处是它变成纯函数，冒烟能直接断言，不用等手点界面才发现少了一档。
///
/// 【只给词，不给颜色、不给图标】
/// 形态说明的是"这条会怎么对待我"（每天回来 / 到某天为止 / 定点几点），
/// 不是一种需要先背图例再看的分类。四个词用同一档小字写在副标题开头，
/// 扫一眼就读到了；换成四种颜色或四个图标，清单立刻变花，
/// 而"变花"正是这个界面被抱怨过的地方。
/// </summary>
public static class ItemLabels
{
    /// <summary>
    /// 清单行开头的形态词。容器返回 null——它不进今日清单，
    /// 在大任务页里靠缩进和「几 / 几 件完成」自辨，不需要再多一个词。
    ///
    /// 【为什么先读 CompletionMode，再读 Shape】
    /// 「每天」是靠 CompletionMode 声明的，它底下那条 Shape 仍然是 DayTask。
    /// 反过来先读 Shape 会把它说成「今天」，等于把"每天都会回来"讲成"就今天一次"。
    /// 这个读法顺序是 11-22 收分类时定下的：形态显式存，读的时候按声明读。
    /// </summary>
    public static string? ShapeLabel(Item item)
    {
        if (item.CompletionMode == CompletionMode.ByChildren)
        {
            return null;
        }

        if (item.CompletionMode == CompletionMode.PerOccurrence)
        {
            return "每天";
        }

        // 【为什么"今天做"不给词】
        // 这张清单本身就是"今天"，里面每一条都是今天的——再写一遍是废话，
        // 而且会把副标题挤成只有两个字、本该显示的时间/用时反而没了
        // （11-22 22:05 实测：刚加的子任务副标题只剩「今天」）。
        // 四种形态仍然可辨：有「定点」说明带钟点、有「期限」说明只到某天、
        // 有「每天」说明天天回来，**什么都没有的就是今天做**。
        // 这正是 ItemViewModel 里那条老规矩——标记用来说例外，不拿来复述默认。
        // Unanchored 是历史空转值（真实库 0 条），同样不给词。
        return item.Shape switch
        {
            ItemShape.DeadlineOnly => "期限",
            ItemShape.Timed => "定点",
            _ => null
        };
    }
}
