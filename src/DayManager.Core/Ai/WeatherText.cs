using System.Text.Json;

namespace DayManager.Core.Ai;

/// <summary>
/// 把 Open-Meteo 的天气数据拼成中文。
///
/// 【为什么天气不走网页搜索，要单独接 Open-Meteo】
/// 网页搜索（博查）对「北京天气」这种查询，经常返回报纸首页/版面链接，
/// 没有能读的正文——模型拿不到温度，只能说实话「拿不到」。
/// Open-Meteo 返回的是**结构化天气数据**（温度、天气代码），免费、不要 key，
/// 直接拼成中文，一次就稳。
///
/// 【为什么放 Core】
/// 拼文字是纯函数（进 JSON、出中文），符合 Core「零依赖纯逻辑」的规矩，
/// 冒烟能直接喂 JSON 验证，不用真调一次天气接口。
/// </summary>
public static class WeatherText
{
    /// <summary>
    /// 把地理编码接口的响应解析出经纬度。找不到返回 null。
    /// </summary>
    public static (double Lat, double Lon)? ParseGeo(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (!doc.RootElement.TryGetProperty("results", out var results)
                || results.ValueKind != JsonValueKind.Array
                || results.GetArrayLength() == 0)
            {
                return null;
            }

            var first = results[0];
            if (!first.TryGetProperty("latitude", out var lat) || !first.TryGetProperty("longitude", out var lon))
            {
                return null;
            }

            return (lat.GetDouble(), lon.GetDouble());
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// WMO 天气代码 → 中文。只覆盖常见的几类，其余落回「多云」。
    /// </summary>
    public static string MapCode(int code) => code switch
    {
        0 => "晴",
        1 or 2 => "晴间多云",
        3 => "多云",
        45 or 48 => "有雾",
        51 or 53 or 55 => "毛毛雨",
        56 or 57 or 66 or 67 => "冻雨",
        61 or 63 or 65 => "有雨",
        71 or 73 or 75 => "有雪",
        77 => "雨夹雪",
        80 or 81 or 82 => "阵雨",
        85 or 86 => "阵雪",
        95 => "雷阵雨",
        96 or 99 => "雷暴伴冰雹",
        _ => "多云"
    };

    /// <summary>
    /// 把预报响应拼成一句话。当前温度、天气，加上今天白天最高/夜里最低。
    /// 缺哪块就少说哪块，绝不编一个数字。
    /// </summary>
    public static string FormatForecast(string body, string location)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);

            double? curTemp = null;
            int curCode = -1;
            if (doc.RootElement.TryGetProperty("current", out var cur))
            {
                curTemp = cur.TryGetProperty("temperature_2m", out var t) ? t.GetDouble() : null;
                curCode = cur.TryGetProperty("weather_code", out var c) ? c.GetInt32() : -1;
            }

            double? max = null;
            double? min = null;
            int dayCode = -1;
            if (doc.RootElement.TryGetProperty("daily", out var daily))
            {
                max = daily.TryGetProperty("temperature_2m_max", out var mx) && mx.GetArrayLength() > 0
                    ? mx[0].GetDouble() : null;
                min = daily.TryGetProperty("temperature_2m_min", out var mn) && mn.GetArrayLength() > 0
                    ? mn[0].GetDouble() : null;
                dayCode = daily.TryGetProperty("weather_code", out var dc) && dc.GetArrayLength() > 0
                    ? dc[0].GetInt32() : -1;
            }

            var parts = new List<string>();
            parts.Add($"{location}今天{MapCode(dayCode >= 0 ? dayCode : curCode)}");
            if (curTemp is { } ct)
            {
                parts.Add($"当前 {ct:0}°C");
            }
            if (max is { } ma && min is { } mi)
            {
                parts.Add($"白天最高 {ma:0}°C，夜里最低 {mi:0}°C");
            }

            return parts.Count == 1
                ? $"（{location}的天气数据不全，只查到是{MapCode(curCode)}。）"
                : string.Join("，", parts) + "。";
        }
        catch (JsonException)
        {
            return "（天气数据解析不了。）";
        }
    }
}
