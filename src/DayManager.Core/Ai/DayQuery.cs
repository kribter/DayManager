using System.Globalization;

namespace DayManager.Core.Ai;

/// <summary>
/// 把 AI 说的「查哪天／哪段」变成一段确定的、有边界的日期范围。
///
/// 【为什么解析放在 Core】
/// 这是纯函数：输入两个字符串，输出日期范围，不碰数据库也不碰时钟。
/// 放 Core 让冒烟能直接断言边界（没给日期、倒着写、一次查一年），
/// 不用真调一次大模型才发现它写了个离谱的范围。
///
/// 【为什么要有上限】
/// 模型一句话就能要「查去年一整年」。真把三百多天的清单拼进上下文，
/// token 会爆、回复会变慢，而且它多半也用不上那么远的事。
/// 与其报错让它重问一轮，不如裁到上限并如实说明裁了哪一段。
///
/// 【这里不做的事】
/// 日期归属仍按日历日（跨午夜按凌晨 4 点切是另一件事，见 ChatTime 的说明）。
/// 查询取回来的清单口径由 ItemRepository 决定，这里只管"查哪几天"。
/// </summary>
public static class DayQuery
{
    /// <summary>一次查询最多覆盖多少天（含首尾）。</summary>
    public const int MaxSpanDays = 31;

    public sealed record Spec(DateOnly From, DateOnly To, bool Valid, string? Error, bool Clipped)
    {
        public int Days => To.DayNumber - From.DayNumber + 1;

        public IEnumerable<DateOnly> Enumerate()
        {
            for (var d = From; d <= To; d = d.AddDays(1))
            {
                yield return d;
            }
        }
    }

    /// <summary>
    /// date 是起始日（必填），dateTo 是结束日（可空 = 只查这一天）。
    /// 写歪的地方一律接住而不是报错：让模型重问一轮的代价，比少查一段高。
    /// </summary>
    public static Spec Parse(string? date, string? dateTo)
    {
        if (!TryDay(date, out var from))
        {
            return new Spec(default, default, false, "没给要查哪天，或日期不是 yyyy-MM-dd", false);
        }

        // 结束日给了但写歪了，就当只查这一天。
        var to = TryDay(dateTo, out var parsedTo) ? parsedTo : from;

        // 倒着写（把晚的日子放在 date）也接住，别让一次笔误变成「查不到」。
        if (to < from)
        {
            (from, to) = (to, from);
        }

        var clipped = false;
        if (to.DayNumber - from.DayNumber + 1 > MaxSpanDays)
        {
            to = from.AddDays(MaxSpanDays - 1);
            clipped = true;
        }

        return new Spec(from, to, true, null, clipped);
    }

    private static bool TryDay(string? text, out DateOnly day) =>
        DateOnly.TryParseExact(text?.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture,
            DateTimeStyles.None, out day);
}
