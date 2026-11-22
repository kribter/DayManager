using DayManager.Core.Entities;
using DayManager.Core.Enums;

namespace DayManager.Core.Planning;

/// <summary>
/// 大任务详情页里「一层层往下挂」的那棵树，在内存里组装出来的一行。
///
/// 【为什么是行而不是嵌套节点】
/// 界面用的是一个平铺的 CollectionView，靠缩进表达层级，
/// 比嵌套的 DataTemplate / TreeView 少一整层控件和模板切换，
/// 也更贴近用户说的「UI 有点繁杂」——能少一个控件就少一个。
/// </summary>
/// <param name="Item">这一行对应的记录。</param>
/// <param name="Depth">第几层，0 = 顶层阶段或直接挂在大任务下的叶子。</param>
/// <param name="DoneLeaves">这个容器底下完成了几片叶子（只有容器行用得到）。</param>
/// <param name="TotalLeaves">这个容器底下有几片叶子要算进度，放弃的不算（只有容器行用得到）。</param>
public sealed record ProjectTreeRow(
    Item Item,
    int Depth,
    int DoneLeaves,
    int TotalLeaves);

/// <summary>
/// 把「一个项目下的所有记录」按 ParentTaskId 组装成带层级的行。
///
/// 【为什么放 Core 而不是界面层】
/// 和 ItemPresence / ItemAxes 同一个理由：纯函数放在 Core，冒烟能直接测，
/// 将来 Android 端要画同一棵树也不用再写一遍。界面只负责把 Depth 变成缩进。
///
/// 【为什么容器不是一种「事情」】
/// 容器靠 CompletionMode.ByChildren 认（唯一判据在 ItemAxes.IsContainer），
/// 层级靠 ParentTaskId 自引用表达。这两样都不是枚举里的一个类型值，
/// 所以这棵树只是「同一批记录的一种摆法」，库里不用为此加字段或迁移。
/// </summary>
public static class ProjectTree
{
    /// <summary>脏数据兜底：再深也不往下走了，避免一条坏记录把界面拖死。</summary>
    private const int MaxDepth = 8;

    public static List<ProjectTreeRow> Flatten(IReadOnlyList<Item> items)
    {
        var (children, roots) = BuildChildren(items);
        var rows = new List<ProjectTreeRow>();
        var visited = new HashSet<Guid>();

        void Walk(Item item, int depth)
        {
            if (!visited.Add(item.Id))
            {
                return; // 防环：脏数据里 A 挂 B、B 又挂 A
            }

            var (done, total) = CountLeaves(item.Id, children, new HashSet<Guid>());
            rows.Add(new ProjectTreeRow(item, depth, done, total));

            if (depth >= MaxDepth)
            {
                return;
            }

            if (children.TryGetValue(item.Id, out var kids))
            {
                foreach (var kid in kids)
                {
                    Walk(kid, depth + 1);
                }
            }
        }

        foreach (var root in roots)
        {
            Walk(root, 0);
        }

        // 有环时，环上的节点既不会被当成 root，也不会被 Walk 到。
        // 补挂到顶层，宁可显示得怪一点，也不能凭空消失。
        foreach (var item in items)
        {
            if (!visited.Contains(item.Id))
            {
                Walk(item, 0);
            }
        }

        return rows;
    }

    /// <summary>
    /// 给「打完勾就地刷新容器行文案」用：返回每个容器底下叶子的完成情况。
    ///
    /// 【为什么单独再给一个入口，而不是让界面拿 Flatten 的结果】
    /// 详情页打勾只更新那一行、不重查数据库（滚动位置不跳）。
    /// 可容器行那句「1 / 3 件完成」在别处，得就地重算；
    /// 把这个算法也放 Core，界面就不必自己再抄一遍「只数叶子、放弃不算分母」。
    /// </summary>
    public static Dictionary<Guid, (int Done, int Total)> StageProgressOf(
        IReadOnlyList<Item> items)
    {
        var (children, _) = BuildChildren(items);
        var result = new Dictionary<Guid, (int Done, int Total)>();

        foreach (var item in items)
        {
            if (!ItemAxes.IsContainer(item))
            {
                continue;
            }

            result[item.Id] = CountLeaves(item.Id, children, new HashSet<Guid>());
        }

        return result;
    }

    private static (Dictionary<Guid, List<Item>> Children, List<Item> Roots) BuildChildren(
        IReadOnlyList<Item> items)
    {
        var ids = items.Select(x => x.Id).ToHashSet();
        var children = new Dictionary<Guid, List<Item>>();
        var roots = new List<Item>();

        // 同级顺序沿用传入顺序。GetItemsAsync 已经排好了：
        // 没做完的在前、再按截止日和 SortOrder，所以这里不再另排一次，
        // 免得详情页和列表页的顺序对不上。
        foreach (var item in items)
        {
            if (item.ParentTaskId is { } parentId && ids.Contains(parentId))
            {
                if (!children.TryGetValue(parentId, out var list))
                {
                    list = new List<Item>();
                    children[parentId] = list;
                }

                list.Add(item);
            }
            else
            {
                // 父记录不在这一批里（被删了、或者父挂在别的项目下）：
                // 当顶层处理，绝不把整条记录弄丢——悄悄丢数据比多缩进一层糟得多。
                roots.Add(item);
            }
        }

        return (children, roots);
    }

    /// <summary>
    /// 递归数一个容器底下的叶子：容器自己不算，放弃的不算分母。
    /// 和列表页「进度只数叶子、放弃不计分母」同一口径。
    /// </summary>
    private static (int Done, int Total) CountLeaves(
        Guid containerId,
        IReadOnlyDictionary<Guid, List<Item>> children,
        HashSet<Guid> path)
    {
        if (!path.Add(containerId) || !children.TryGetValue(containerId, out var kids))
        {
            return (0, 0);
        }

        var done = 0;
        var total = 0;

        foreach (var kid in kids)
        {
            if (ItemAxes.IsContainer(kid))
            {
                var (childDone, childTotal) = CountLeaves(kid.Id, children, path);
                done += childDone;
                total += childTotal;
            }
            else if (kid.Status != ItemStatus.Abandoned)
            {
                total++;
                if (kid.Status == ItemStatus.Done)
                {
                    done++;
                }
            }
        }

        path.Remove(containerId);
        return (done, total);
    }
}
