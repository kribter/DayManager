using DayManager.App.ViewModels;
using DayManager.Core.Enums;
using DayManager.Data.Repositories;

namespace DayManager.App.Services;

/// <summary>
/// 一条任务的"更多"菜单：重命名、删除、改状态。
///
/// 【为什么这些逻辑不住在 MainViewModel 里了】
/// 大任务详情页也要对子任务用同一套菜单。
/// 把这一百行复制一份是最快的做法，但代价是：
/// 将来加一个"移到另一天"要改两处，改漏一处就变成"在日程页能做、在详情页做不了"——
/// 用户只会觉得软件莫名其妙，不会想到是两个菜单不同步。
/// 所以抽出来，两页共用同一个实现。
///
/// 【为什么它要一个 onChanged 回调，而不是自己刷新页面】
/// 它不知道调用方是怎么显示数据的：日程页要按当前日期重查，
/// 详情页要按项目重查。刷新动作只能由调用方给，
/// 硬写一种会让另一个页面刷错。
/// </summary>
public class ItemActionService
{
    private readonly ItemRepository _items;

    public ItemActionService(ItemRepository items)
    {
        _items = items;
    }

    /// <summary>
    /// 弹出菜单并执行选中的动作。
    /// </summary>
    /// <param name="item">被操作的那一行。</param>
    /// <param name="onChanged">动作确实改了数据时调用它刷新界面；没改就不调（比如点了取消）。</param>
    public async Task ShowMenuAsync(ItemViewModel item, Func<Task>? onChanged = null)
    {
        var page = CurrentPage;
        if (page is null)
        {
            return;
        }

        var action = await page.DisplayActionSheetAsync(
            item.Title, "取消", null, BuildChoices(item).ToArray());

        if (action is null || action == "取消")
        {
            return;
        }

        var changed = false;

        try
        {
            switch (action)
            {
                case "标记为进行中":
                    await SetStatusAsync(item, ItemStatus.InProgress);
                    changed = true;
                    break;

                case "标记完成":
                    await SetStatusAsync(item, ItemStatus.Done);
                    changed = true;
                    break;

                case "改回待办":
                    await SetStatusAsync(item, ItemStatus.Pending);
                    changed = true;
                    break;

                case MenuSkip:
                    changed = await SkipAsync(page, item);
                    break;

                case MenuAbandon:
                    changed = await AbandonAsync(page, item);
                    break;

                case "还是要做（改回待办）":
                    await SetStatusAsync(item, ItemStatus.Pending);
                    changed = true;
                    break;

                case "重命名":
                    changed = await RenameAsync(page, item);
                    break;

                case "删除":
                    changed = await DeleteAsync(page, item);
                    break;
            }
        }
        catch (Exception ex)
        {
            await ReportErrorAsync(ex);
            return;
        }

        if (changed && onChanged is not null)
        {
            await onChanged();
        }
    }

    // 菜单项的文字做成常量：switch 里要按同一个字符串判断，
    // 两边各写一次字面量的话，改文案时忘了改另一处就会静默失效——
    // 点了没反应，还不报错，是最难查的一类 bug。
    private const string MenuSkip = "今天先跳过（明天再来）";
    private const string MenuAbandon = "放弃不做了";

    /// <summary>
    /// 菜单项按当前状态动态生成：已经完成的条目不该再显示"标记完成"。
    /// </summary>
    private static List<string> BuildChoices(ItemViewModel item)
    {
        var choices = new List<string>();

        if (item.Item.Status != ItemStatus.InProgress && item.Item.Status != ItemStatus.Done)
        {
            choices.Add("标记为进行中");
        }

        if (item.Item.Status != ItemStatus.Done)
        {
            choices.Add("标记完成");
        }
        else
        {
            choices.Add("改回待办");
        }

        //
        // 【为什么"跳过"和"放弃"都要在，而且措辞要自带后果】
        // 两者在界面上都是"今天不做它"，后果完全相反：
        //   跳过 = 明天照常回来（我今天有事，不是不做）
        //   放弃 = 永远不再出现（我承认不做了）
        // 混成一个选项的后果是：用户以为只是今天不做，结果那条任务彻底消失了；
        // 或者以为放弃了，结果它天天回来烦他。
        //
        if (item.Item.Status != ItemStatus.Done && item.Item.Status != ItemStatus.Abandoned)
        {
            choices.Add(MenuSkip);
            choices.Add(MenuAbandon);
        }
        else if (item.Item.Status == ItemStatus.Abandoned)
        {
            choices.Add("还是要做（改回待办）");
        }

        choices.Add("重命名");
        choices.Add("删除");

        return choices;
    }

    private async Task SetStatusAsync(ItemViewModel item, ItemStatus status)
    {
        var updated = await _items.SetStatusAsync(item.Id, status);
        if (updated is not null)
        {
            item.Update(updated);
        }
    }

    /// <summary>
    /// 跳过：今天不做，明天照常回来。
    ///
    /// 【为什么要追问一句原因】
    /// 可留空——强制填写的话，"只是不想动"这个真实原因就写不进来，
    /// 而它恰恰是后面最需要被看见的那个。
    /// </summary>
    private async Task<bool> SkipAsync(Page page, ItemViewModel item)
    {
        var reason = await page.DisplayPromptAsync(
            "今天先跳过", "为什么不做了？（可以留空）", "跳过", "取消",
            placeholder: "生病 / 有事 / 被别的事占了");

        // DisplayPromptAsync 点取消返回 null，必须判掉：
        // 否则"取消"会被当成"留空的确认"，任务照样被跳过。
        if (reason is null)
        {
            return false;
        }

        await _items.SetStatusAsync(item.Id, ItemStatus.Skipped, reason);
        return true;
    }

    private async Task<bool> AbandonAsync(Page page, ItemViewModel item)
    {
        // 放弃是五态里影响最远的（它永不出现），所以多问一句。
        // 措辞不带指责：写的是"不做了"，不是"放弃吧"。
        var giveUp = await page.DisplayAlertAsync(
            "放弃", $"「{item.Title}」标记为不做了，之后不会再出现在清单里。", "不做了", "再想想");

        if (!giveUp)
        {
            return false;
        }

        await _items.SetStatusAsync(item.Id, ItemStatus.Abandoned, null);
        return true;
    }

    private async Task<bool> RenameAsync(Page page, ItemViewModel item)
    {
        var newName = await page.DisplayPromptAsync("重命名", "改成：", initialValue: item.Title);

        if (string.IsNullOrWhiteSpace(newName))
        {
            return false;
        }

        await _items.RenameAsync(item.Id, newName);
        return true;
    }

    /// <summary>
    /// 删除是唯一不可逆的选项，所以多问一句。
    /// 而且问的是"确定删掉「标题」"，带上具体内容，
    /// 比干巴巴的"确定删除吗"更能防止误操作。
    /// </summary>
    private async Task<bool> DeleteAsync(Page page, ItemViewModel item)
    {
        var confirmed = await page.DisplayAlertAsync(
            "删除", $"确定删掉「{item.Title}」？", "删除", "取消");

        if (!confirmed)
        {
            return false;
        }

        await _items.SoftDeleteAsync(item.Id);
        return true;
    }

    private static Page? CurrentPage =>
        Application.Current?.Windows.Count > 0 ? Application.Current.Windows[0].Page : null;

    private static async Task ReportErrorAsync(Exception ex)
    {
        var page = CurrentPage;
        if (page is not null)
        {
            await page.DisplayAlertAsync("出错了", ex.Message, "好");
        }
    }
}
