using DayManager.App.Services;
using DayManager.App.ViewModels;

namespace DayManager.App.Drawables;

/// <summary>
/// 时间页的柱状图。
///
/// 【为什么自己画，不用第三方图表库】
/// 要画的东西只有一件事：一排高度不同的柱子加几个刻度。
/// 引一个图表库要带来几百 KB 依赖、一套配置对象、以及"它的默认样式和这个界面不像"的返工。
/// GraphicsView + IDrawable 是 MAUI 自带的，几十行就够，还能跟着主题走。
///
/// 【为什么柱子不画数值】
/// 数值在下面的应用排行和总时长里都有。柱子上再标一遍，
/// 24 根柱子会糊成一片数字，反而看不出"哪几个小时在电脑前"这个形状。
/// 图看形状，数字看清单——各干一件事。
/// </summary>
public sealed class UsageBarsDrawable : IDrawable
{
    public IReadOnlyList<BarItem> Bars { get; set; } = Array.Empty<BarItem>();

    /// <summary>没有数据时显示的说明。</summary>
    public string EmptyText { get; set; } = "这段时间还没有记录";

    public void Draw(ICanvas canvas, RectF dirtyRect)
    {
        if (dirtyRect.Width <= 0 || dirtyRect.Height <= 0)
        {
            return;
        }

        var bars = Bars;
        var max = bars.Count == 0 ? 0 : bars.Max(x => x.Seconds);

        var palette = Palette.Load();

        if (bars.Count == 0 || max == 0)
        {
            canvas.FontColor = palette.Label;
            canvas.FontSize = 12;
            canvas.DrawString(
                EmptyText,
                dirtyRect.X,
                dirtyRect.Y + (dirtyRect.Height / 2) - 10,
                dirtyRect.Width,
                20,
                HorizontalAlignment.Center,
                VerticalAlignment.Center);
            return;
        }

        const float LabelHeight = 16f;
        var baseline = dirtyRect.Bottom - LabelHeight;
        var chartHeight = dirtyRect.Height - LabelHeight - 4;
        var slot = dirtyRect.Width / bars.Count;

        // 柱子占格子的 62%：留出的空隙是"一根一根"的视觉依据，
        // 挨在一起就连成一片色块了，看不出是几个时间段。
        var barWidth = Math.Max(2f, slot * 0.62f);

        // 基线：没有它，全是矮柱子的那几天会看着像"页面坏了"。
        canvas.StrokeColor = palette.Baseline;
        canvas.StrokeSize = 1;
        canvas.DrawLine(dirtyRect.X, baseline, dirtyRect.Right, baseline);

        for (var i = 0; i < bars.Count; i++)
        {
            var bar = bars[i];
            var x = dirtyRect.X + (slot * i) + ((slot - barWidth) / 2);

            if (bar.Seconds > 0)
            {
                // 最矮的柱子也给 2 像素：不到一分钟的记录，
                // 画成 0 高度等于告诉用户"这里什么都没发生"，那是错的。
                var height = Math.Max(2f, chartHeight * bar.Seconds / max);
                canvas.FillColor = palette.Bar;
                canvas.FillRoundedRectangle(x, baseline - height, barWidth, height, 2);
            }

            if (!string.IsNullOrEmpty(bar.Label))
            {
                canvas.FontColor = palette.Label;
                canvas.FontSize = 9;
                canvas.DrawString(
                    bar.Label,
                    x - (slot / 2),
                    baseline + 2,
                    slot * 2,
                    LabelHeight,
                    HorizontalAlignment.Center,
                    VerticalAlignment.Top);
            }
        }
    }

    /// <summary>
    /// 图表用的几个颜色。
    ///
    /// 【为什么从资源字典里取，而不是写死 #3F6FB5】
    /// 主色是会被改的（用户说过界面要低刺激）。写死之后改了主题色，
    /// 图表是唯一不跟着变的地方，而这种"就它不一样"的偏差最难发现。
    /// 取不到时退回硬编码值，保证画得出来。
    /// </summary>
    private readonly record struct Palette(Color Bar, Color Label, Color Baseline)
    {
        public static Palette Load()
        {
            var resources = Application.Current?.Resources;

            return new Palette(
                // 深色下三个值都要换：主色在黑底上偏暗、次要字的灰近乎隐形，
                // 而基线反过来要压暗——浅色里它是淡淡的辅助线，深色里 #C8C8C8 会抢戏。
                Bar: Get(resources, AppColors.IsDark ? "PrimaryDark" : "Primary",
                         AppColors.IsDark ? Color.FromArgb("#9DB8DE") : Color.FromArgb("#3F6FB5")),
                Label: Get(resources, AppColors.IsDark ? "TextMutedDark" : "TextMutedLight",
                           AppColors.IsDark ? Color.FromArgb("#A2A2A2") : Color.FromArgb("#8A8A8A")),
                Baseline: Get(resources, AppColors.IsDark ? "Gray600" : "Gray200",
                              AppColors.IsDark ? Color.FromArgb("#404040") : Color.FromArgb("#C8C8C8")));
        }

        private static Color Get(ResourceDictionary? resources, string key, Color fallback) =>
            resources is not null && resources.TryGetValue(key, out var value) && value is Color color
                ? color
                : fallback;
    }
}
