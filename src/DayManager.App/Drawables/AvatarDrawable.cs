using DayManager.App.Services;
using Microsoft.Maui.Graphics;

namespace DayManager.App.Drawables;

/// <summary>
/// 谈话某一侧的头像圆：选了图就画那张图，没选就画默认图形。
///
/// 【为什么是画出来的，而不是一个 Image 控件加圆形裁剪】
/// WinUI 的 <c>UIElement.Clip</c> 只接受矩形，椭圆裁上去不生效——
/// Windows 上会露出一个方角，而 Android 上同一份 XAML 却是圆的。
/// 自己画是两端唯一一致的做法：先裁一个圆形的画布，再把图铺进去，
/// 露在外面的永远是圆的。
///
/// 【为什么不把默认头像做成图片资源】
/// 它要给两种主题用（浅色下的灰底、深色下的深灰底），
/// 做成图片得准备两套，而且以后改主色还会对不上。
/// 画的话颜色直接读 <see cref="AppColors"/>，主题一换自动跟着变。
///
/// 【一边一支笔】
/// 一个实例只画一个槽（AI 或你自己）。两侧各拿一支，
/// 比起"画的时候再传参数知道该画谁"少一层判断。
/// </summary>
public sealed class AvatarDrawable : IDrawable
{
    private readonly AvatarSlot _slot;

    public AvatarDrawable(AvatarSlot slot) => _slot = slot;

    public void Draw(ICanvas canvas, RectF dirtyRect)
    {
        var size = Math.Min(dirtyRect.Width, dirtyRect.Height);
        if (size <= 0)
        {
            return;
        }

        canvas.SaveState();

        var circle = new PathF();
        circle.AppendEllipse(0, 0, size, size);
        canvas.ClipPath(circle);

        var custom = _slot.Custom;
        if (custom is { Width: > 0, Height: > 0 })
        {
            // 铺满（AspectFill）：图片不一定是正方形，按长边撑满、多的两边各切一半。
            // 不这么算的话，一张竖图会在圆里缩成中间一条。
            var scale = Math.Max(size / custom.Width, size / custom.Height);
            var w = custom.Width * scale;
            var h = custom.Height * scale;

            canvas.DrawImage(custom, (size - w) / 2, (size - h) / 2, w, h);
        }
        else if (_slot.Fallback == DefaultAvatar.Calendar)
        {
            DrawCalendar(canvas, size);
        }
        else
        {
            DrawPerson(canvas, size);
        }

        canvas.RestoreState();
    }

    /// <summary>
    /// AI 那侧的默认图形：浅底圆 + 三列两行的格子，其中一格是实的（今天）。
    ///
    /// 【为什么格子的间隔按尺寸比例算，而不是写死几个像素】
    /// 这个圆在对话页是 26、在设置页是 36，还要乘屏幕缩放。
    /// 写死像素的话，小尺寸下间隔比格子还宽，就散成六个点了。
    /// </summary>
    private static void DrawCalendar(ICanvas canvas, float size)
    {
        canvas.FillColor = AppColors.BubbleAi;
        canvas.FillEllipse(0, 0, size, size);

        var pad = size * 0.25f;
        var cellW = (size - pad * 2) / 3f;
        var cellH = (size - pad * 2) / 2f;
        var gap = Math.Max(0.8f, size * 0.05f);
        var corner = Math.Max(0.5f, size * 0.035f);

        var solid = AppColors.Primary;
        var faint = solid.WithAlpha(0.35f);

        for (var row = 0; row < 2; row++)
        {
            for (var col = 0; col < 3; col++)
            {
                // 亮的是上排中间那格：一眼能看见中间有个重点，
                // 放在角上会像是缺了一块。
                var isToday = row == 0 && col == 1;
                canvas.FillColor = isToday ? solid : faint;

                canvas.FillRoundedRectangle(
                    pad + (col * cellW) + (gap / 2),
                    pad + (row * cellH) + (gap / 2),
                    cellW - gap,
                    cellH - gap,
                    corner);
            }
        }
    }

    /// <summary>
    /// 你自己那侧的默认图形：一个人影（头部小圆 + 肩膀的弧）。
    ///
    /// 【为什么是灰色而不是主蓝】
    /// AI 那侧已经是蓝的（它配主色，像一个功能入口）。
    /// 两边都用主色就分不出主次了；你这一侧用中性灰，
    /// 一眼看过去蓝的那边是系统，灰的那边是人。
    ///
    /// 【为什么肩膀要画成一个超出圆的大椭圆】
    /// 头肩的样子本来就是一个圆加一个半圆，
    /// 但把"半"这件事算准很啰嗦——直接画一个大椭圆往下伸，
    /// 被外面的圆形裁剪切掉多余部分，边缘永远是干净的弧。
    /// </summary>
    private static void DrawPerson(ICanvas canvas, float size)
    {
        canvas.FillColor = AppColors.BubbleAi;
        canvas.FillEllipse(0, 0, size, size);

        canvas.FillColor = AppColors.Muted;

        var head = size * 0.17f;
        canvas.FillEllipse((size / 2) - head, size * 0.21f, head * 2, head * 2);

        var shoulder = size * 0.66f;
        canvas.FillEllipse((size / 2) - (shoulder / 2), size * 0.5f, shoulder, shoulder);
    }
}
