using System.Globalization;
using DayManager.Core.Ai;

namespace DayManager.Api.Services;

/// <summary>
/// 天气查询：城市名 → 经纬度 → 预报 → 中文一句话。
///
/// 【为什么天气单独一个服务，不走网页搜索】
/// 网页搜索对「北京天气」经常返回报纸首页/版面链接，没有可读的温度数据，
/// 模型只能回「拿不到」。Open-Meteo 是免费、不要 key 的天气接口，
/// 返回结构化数据（温度、天气代码），拼成中文一次就稳。
/// </summary>
public sealed class WeatherService
{
    private const string GeoUrl = "https://geocoding-api.open-meteo.com/v1/search";
    private const string ForecastUrl = "https://api.open-meteo.com/v1/forecast";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<WeatherService> _logger;

    public WeatherService(IHttpClientFactory httpClientFactory, ILogger<WeatherService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public async Task<string> GetAsync(string location, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(location))
        {
            return "（没给地点。）";
        }

        var client = _httpClientFactory.CreateClient();
        // Open-Meteo 在国内能通但慢（实测 15 秒不够、30 秒才回），给足 45 秒。
        client.Timeout = TimeSpan.FromSeconds(45);

        // ① 城市名 → 经纬度。中文地名直接能查。
        var geoQuery = $"{GeoUrl}?name={Uri.EscapeDataString(location)}&count=1&language=zh&format=json";
        string geoBody;
        try
        {
            geoBody = await client.GetStringAsync(geoQuery, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            _logger.LogWarning(ex, "天气地理编码失败");
            return "（没查到这个地方。）";
        }

        var geo = WeatherText.ParseGeo(geoBody);
        if (geo is null)
        {
            return "（没查到这个地方。）";
        }

        // ② 经纬度 → 预报。只要今天：当前温度/天气 + 白天最高/夜里最低。
        var forecastQuery = $"{ForecastUrl}?latitude={geo.Value.Lat.ToString(CultureInfo.InvariantCulture)}"
            + $"&longitude={geo.Value.Lon.ToString(CultureInfo.InvariantCulture)}"
            + "&current=temperature_2m,weather_code"
            + "&daily=temperature_2m_max,temperature_2m_min,weather_code"
            + "&timezone=auto&forecast_days=1";
        string forecastBody;
        try
        {
            forecastBody = await client.GetStringAsync(forecastQuery, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            _logger.LogWarning(ex, "天气查询失败");
            return "（天气接口没返回。）";
        }

        return WeatherText.FormatForecast(forecastBody, location);
    }
}
