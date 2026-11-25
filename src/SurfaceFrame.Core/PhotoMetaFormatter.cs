using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SurfaceFrame;

public sealed record PhotoMetadata(string? Date, string? City, string? Device)
{
    public bool IsEmpty => string.IsNullOrEmpty(Date) && string.IsNullOrEmpty(City) && string.IsNullOrEmpty(Device);
}

public static partial class PhotoMetaFormatter
{
    readonly record struct GeoCity(double Lat, double Lon, double RadiusKm, string Name);

    // Specific towns/resorts with tighter radius come before larger prefecture cities where they overlap.
    static readonly GeoCity[] KnownGeoCities =
    {
        // China - Famous resorts / districts first
        new(40.96, 115.28, 35, "崇礼"),
        new(18.25, 109.51, 55, "三亚"),
        new(30.13, 118.17, 45, "黄山"),
        new(29.32, 110.48, 50, "张家界"),
        new(27.95, 109.60, 35, "凤凰"),
        new(25.60, 100.27, 45, "大理"),
        new(26.87, 100.23, 55, "丽江"),
        new(27.83, 99.71, 55, "香格里拉"),
        new(33.26, 103.92, 55, "九寨沟"),
        new(29.05, 100.30, 60, "稻城"),
        new(40.14, 94.66, 65, "敦煌"),
        new(31.74, 110.68, 45, "神农架"),
        new(29.25, 117.86, 40, "婺源"),
        new(27.76, 118.03, 45, "武夷山"),
        new(37.20, 112.18, 35, "平遥"),

        // China - Major cities & capitals
        new(30.59, 114.30, 75, "武汉"),
        new(39.90, 116.40, 80, "北京"),
        new(31.23, 121.47, 60, "上海"),
        new(23.13, 113.26, 55, "广州"),
        new(22.54, 114.06, 35, "深圳"),
        new(22.32, 114.17, 25, "香港"),
        new(22.20, 113.54, 15, "澳门"),
        new(24.48, 118.09, 45, "厦门"),
        new(30.27, 120.15, 65, "杭州"),
        new(32.06, 118.80, 60, "南京"),
        new(31.30, 120.58, 50, "苏州"),
        new(31.49, 120.31, 40, "无锡"),
        new(31.81, 119.97, 40, "常州"),
        new(32.39, 119.41, 45, "扬州"),
        new(34.60, 119.22, 65, "连云港"),
        new(34.21, 117.28, 55, "徐州"),
        new(32.01, 120.86, 50, "南通"),
        new(34.26, 108.95, 65, "西安"),
        new(30.67, 104.07, 65, "成都"),
        new(29.56, 106.55, 70, "重庆"),
        new(28.23, 112.94, 60, "长沙"),
        new(34.75, 113.62, 60, "郑州"),
        new(39.13, 117.20, 60, "天津"),
        new(36.07, 120.38, 60, "青岛"),
        new(36.65, 117.12, 55, "济南"),
        new(37.46, 121.45, 55, "烟台"),
        new(37.51, 122.12, 50, "威海"),
        new(29.87, 121.54, 55, "宁波"),
        new(28.00, 120.70, 55, "温州"),
        new(30.00, 120.58, 45, "绍兴"),
        new(30.75, 120.75, 45, "嘉兴"),
        new(30.02, 122.21, 50, "舟山"),
        new(26.07, 119.30, 55, "福州"),
        new(24.91, 118.59, 45, "泉州"),
        new(24.51, 117.65, 45, "漳州"),
        new(20.04, 110.20, 55, "海口"),
        new(25.04, 102.71, 65, "昆明"),
        new(25.27, 110.29, 60, "桂林"),
        new(22.82, 108.37, 60, "南宁"),
        new(26.65, 106.63, 60, "贵阳"),
        new(31.82, 117.23, 60, "合肥"),
        new(28.68, 115.86, 60, "南昌"),
        new(29.71, 116.00, 50, "九江"),
        new(29.27, 117.18, 45, "景德镇"),
        new(30.69, 111.29, 60, "宜昌"),
        new(32.01, 112.12, 55, "襄阳"),
        new(30.33, 112.24, 55, "荆州"),
        new(30.27, 109.49, 60, "恩施"),
        new(22.27, 113.58, 35, "珠海"),
        new(23.02, 113.12, 35, "佛山"),
        new(23.02, 113.75, 35, "东莞"),
        new(38.91, 121.61, 60, "大连"),
        new(41.80, 123.43, 65, "沈阳"),
        new(43.82, 125.32, 65, "长春"),
        new(45.80, 126.53, 65, "哈尔滨"),
        new(37.87, 112.55, 60, "太原"),
        new(40.08, 113.30, 55, "大同"),
        new(38.04, 114.51, 60, "石家庄"),
        new(39.94, 119.60, 55, "秦皇岛"),
        new(40.82, 114.88, 60, "张家口"),
        new(40.95, 117.96, 55, "承德"),
        new(34.62, 112.45, 55, "洛阳"),
        new(34.80, 114.31, 45, "开封"),
        new(40.84, 111.75, 65, "呼和浩特"),
        new(36.06, 103.83, 65, "兰州"),
        new(36.62, 101.78, 65, "西宁"),
        new(38.49, 106.23, 65, "银川"),
        new(43.83, 87.62, 70, "乌鲁木齐"),
        new(29.65, 91.17, 70, "拉萨"),
        new(29.65, 94.36, 70, "林芝"),
        new(25.03, 121.57, 45, "台北"),
        new(22.63, 120.30, 45, "高雄"),
        new(24.15, 120.67, 45, "台中"),
        new(23.99, 121.60, 45, "花莲"),

        // Japan
        new(35.10, 139.07, 18, "热海"),
        new(35.23, 139.11, 16, "箱根"),
        new(35.32, 139.55, 15, "镰仓"),
        new(35.50, 138.76, 28, "富士山"),
        new(36.35, 138.60, 22, "轻井泽"),
        new(35.44, 139.64, 18, "横滨"),
        new(35.68, 139.77, 45, "东京"),
        new(35.01, 135.77, 25, "京都"),
        new(34.69, 135.80, 20, "奈良"),
        new(34.69, 135.19, 20, "神户"),
        new(34.69, 135.50, 40, "大阪"),
        new(35.18, 136.91, 40, "名古屋"),
        new(43.06, 141.35, 45, "札幌"),
        new(43.19, 140.99, 20, "小樽"),
        new(41.77, 140.73, 30, "函馆"),
        new(43.34, 142.38, 35, "富良野"),
        new(33.59, 130.40, 40, "福冈"),
        new(26.21, 127.68, 55, "冲绳"),
        new(36.56, 136.66, 35, "金泽"),
        new(34.39, 132.46, 35, "广岛"),

        // Asia & Middle East
        new(24.45, 54.38, 55, "阿布扎比"),
        new(25.20, 55.27, 45, "迪拜"),
        new(25.29, 51.53, 45, "多哈"),
        new(12.57, 99.96, 35, "华欣"),
        new(7.88, 98.39, 45, "普吉岛"),
        new(13.76, 100.50, 50, "曼谷"),
        new(18.79, 98.98, 45, "清迈"),
        new(12.93, 100.88, 30, "芭提雅"),
        new(9.51, 100.01, 25, "苏梅岛"),
        new(1.35, 103.82, 35, "新加坡"),
        new(3.14, 101.69, 45, "吉隆坡"),
        new(5.41, 100.33, 30, "槟城"),
        new(5.98, 116.07, 45, "亚庇"),
        new(-8.41, 115.19, 65, "巴厘岛"),
        new(37.57, 126.98, 45, "首尔"),
        new(35.18, 129.08, 40, "釜山"),
        new(33.49, 126.53, 45, "济州"),
        new(21.03, 105.85, 40, "河内"),
        new(10.82, 106.63, 40, "胡志明"),

        // Europe
        new(69.65, 18.96, 45, "特罗姆瑟"),
        new(60.39, 5.32, 45, "卑尔根"),
        new(59.91, 10.75, 45, "奥斯陆"),
        new(68.21, 13.88, 60, "罗弗敦"),
        new(46.52, 6.63, 30, "洛桑"),
        new(46.20, 6.14, 28, "日内瓦"),
        new(47.37, 8.54, 35, "苏黎世"),
        new(46.69, 7.86, 30, "因特拉肯"),
        new(47.05, 8.31, 28, "卢塞恩"),
        new(46.02, 7.75, 25, "采尔马特"),
        new(46.95, 7.45, 25, "伯尔尼"),
        new(47.56, 7.59, 25, "巴塞尔"),
        new(48.86, 2.35, 45, "巴黎"),
        new(43.71, 7.26, 35, "尼斯"),
        new(45.76, 4.84, 35, "里昂"),
        new(51.51, -0.13, 50, "伦敦"),
        new(55.95, -3.19, 35, "爱丁堡"),
        new(45.46, 9.19, 40, "米兰"),
        new(41.90, 12.50, 45, "罗马"),
        new(45.44, 12.32, 35, "威尼斯"),
        new(43.77, 11.26, 35, "佛罗伦萨"),
        new(40.85, 14.27, 35, "那不勒斯"),
        new(48.14, 11.58, 45, "慕尼黑"),
        new(52.52, 13.40, 45, "柏林"),
        new(50.11, 8.68, 40, "法兰克福"),
        new(48.21, 16.37, 45, "维也纳"),
        new(47.81, 13.05, 35, "萨尔茨堡"),
        new(50.08, 14.44, 40, "布拉格"),
        new(47.50, 19.04, 40, "布达佩斯"),
        new(52.37, 4.90, 35, "阿姆斯特丹"),
        new(50.85, 4.35, 35, "布鲁塞尔"),
        new(41.39, 2.17, 40, "巴塞罗那"),
        new(40.42, -3.70, 45, "马德里"),
        new(38.72, -9.14, 40, "里斯本"),
        new(37.98, 23.73, 40, "雅典"),
        new(36.39, 25.46, 25, "圣托里尼"),
        new(41.01, 28.98, 50, "伊斯坦布尔"),
        new(59.33, 18.07, 45, "斯德哥尔摩"),
        new(55.68, 12.57, 40, "哥本哈根"),
        new(60.17, 24.94, 40, "赫尔辛基"),
        new(66.50, 25.73, 45, "罗瓦涅米"),
        new(64.15, -21.94, 45, "雷克雅未克"),

        // Americas & Oceania
        new(40.71, -74.01, 50, "纽约"),
        new(34.05, -118.24, 55, "洛杉矶"),
        new(37.77, -122.42, 45, "旧金山"),
        new(47.61, -122.33, 45, "西雅图"),
        new(42.36, -71.06, 40, "波士顿"),
        new(38.91, -77.04, 40, "华盛顿"),
        new(41.88, -87.63, 45, "芝加哥"),
        new(36.17, -115.14, 45, "拉斯维加斯"),
        new(21.31, -157.86, 50, "夏威夷"),
        new(49.28, -123.12, 45, "温哥华"),
        new(43.65, -79.38, 45, "多伦多"),
        new(-33.87, 151.21, 50, "悉尼"),
        new(-37.81, 144.96, 50, "墨尔本"),
        new(-36.85, 174.76, 45, "奥克兰"),
        new(-45.03, 168.66, 45, "皇后镇"),
    };

    static readonly Dictionary<string, string> PlaceMap = new(StringComparer.OrdinalIgnoreCase)
    {
        // Cities
        ["Wuhan"] = "武汉", ["Xiamen"] = "厦门",
        ["Sanya"] = "三亚", ["Chongli"] = "崇礼", ["Zhangjiakou"] = "张家口",
        ["Lianyungang"] = "连云港", ["Xi'an"] = "西安",
        ["Xian"] = "西安", ["Hangzhou"] = "杭州", ["Beijing"] = "北京",
        ["Shanghai"] = "上海", ["Guangzhou"] = "广州", ["Shenzhen"] = "深圳", ["Chengdu"] = "成都",
        ["Chongqing"] = "重庆", ["Nanjing"] = "南京", ["Suzhou"] = "苏州", ["Changsha"] = "长沙",
        ["Qingdao"] = "青岛", ["Tianjin"] = "天津", ["Zhengzhou"] = "郑州", ["Ningbo"] = "宁波",
        ["Fuzhou"] = "福州", ["Kunming"] = "昆明", ["Guilin"] = "桂林", ["Dali"] = "大理",
        ["Lijiang"] = "丽江", ["Harbin"] = "哈尔滨", ["Dalian"] = "大连", ["Shenyang"] = "沈阳",
        ["Hong Kong"] = "香港", ["Macau"] = "澳门", ["Taipei"] = "台北",
        ["Tokyo"] = "东京", ["Osaka"] = "大阪", ["Izumisano"] = "大阪", ["Kyoto"] = "京都",
        ["Nara"] = "奈良", ["Nara-shi"] = "奈良", ["Kobe"] = "神户", ["Amagasaki"] = "神户",
        ["Atami"] = "热海", ["Narusawa"] = "富士山", ["Hakone"] = "箱根", ["Kamakura"] = "镰仓",
        ["Yokohama"] = "横滨", ["Nagoya"] = "名古屋", ["Sapporo"] = "札幌", ["Otaru"] = "小樽",
        ["Fukuoka"] = "福冈", ["Okinawa"] = "冲绳", ["Naha"] = "冲绳",
        ["Tromsø"] = "特罗姆瑟", ["Tromso"] = "特罗姆瑟", ["Bergen"] = "卑尔根", ["Oslo"] = "奥斯陆",
        ["Lausanne"] = "洛桑", ["Geneva"] = "日内瓦", ["Zurich"] = "苏黎世", ["Zürich"] = "苏黎世",
        ["Interlaken"] = "因特拉肯", ["Lucerne"] = "卢塞恩", ["Zermatt"] = "采尔马特", ["Bern"] = "伯尔尼",
        ["Hua Hin"] = "华欣", ["Phuket"] = "普吉岛", ["Bangkok"] = "曼谷", ["Chiang Mai"] = "清迈",
        ["Abu Dhabi"] = "阿布扎比", ["Musaffah"] = "阿布扎比", ["Dubai"] = "迪拜",
        ["Singapore"] = "新加坡", ["Seoul"] = "首尔", ["Busan"] = "釜山", ["Jeju"] = "济州",
        ["Paris"] = "巴黎", ["Nice"] = "尼斯", ["London"] = "伦敦", ["Milan"] = "米兰",
        ["Rome"] = "罗马", ["Venice"] = "威尼斯", ["Florence"] = "佛罗伦萨", ["Munich"] = "慕尼黑",
        ["Berlin"] = "柏林", ["Vienna"] = "维也纳", ["Prague"] = "布拉格", ["Amsterdam"] = "阿姆斯特丹",
        ["Barcelona"] = "巴塞罗那", ["Madrid"] = "马德里", ["Lisbon"] = "里斯本", ["Athens"] = "雅典",
        ["Istanbul"] = "伊斯坦布尔", ["Stockholm"] = "斯德哥尔摩", ["Copenhagen"] = "哥本哈根",
        ["Helsinki"] = "赫尔辛基", ["Reykjavik"] = "雷克雅未克", ["Reykjavík"] = "雷克雅未克",
        ["New York"] = "纽约", ["Los Angeles"] = "洛杉矶", ["San Francisco"] = "旧金山", ["Seattle"] = "西雅图",
        ["Vancouver"] = "温哥华", ["Toronto"] = "多伦多", ["Sydney"] = "悉尼", ["Melbourne"] = "墨尔本",
    };

    static readonly Dictionary<string, string> RegionMap = new(StringComparer.OrdinalIgnoreCase)
    {
        // Chinese provinces
        ["Hubei"] = "湖北", ["Fujian"] = "福建", ["Hainan"] = "海南", ["Hebei"] = "河北",
        ["Jiangsu"] = "江苏", ["Shaanxi"] = "陕西", ["Zhejiang"] = "浙江", ["Guangdong"] = "广东",
        ["Sichuan"] = "四川", ["Yunnan"] = "云南", ["Hunan"] = "湖南", ["Henan"] = "河南",
        ["Shandong"] = "山东", ["Anhui"] = "安徽", ["Jiangxi"] = "江西", ["Shanxi"] = "山西",
        ["Guangxi"] = "广西", ["Guizhou"] = "贵州", ["Liaoning"] = "辽宁", ["Jilin"] = "吉林",
        ["Heilongjiang"] = "黑龙江", ["Inner Mongolia"] = "内蒙古", ["Gansu"] = "甘肃",
        ["Qinghai"] = "青海", ["Ningxia"] = "宁夏", ["Xinjiang"] = "新疆", ["Tibet"] = "西藏",
        // Japanese prefectures
        ["Tokyo"] = "东京", ["Osaka"] = "大阪", ["Kyoto"] = "京都", ["Nara"] = "奈良",
        ["Hyōgo"] = "兵库", ["Hyogo"] = "兵库", ["Shizuoka"] = "静冈", ["Yamanashi"] = "山梨",
        ["Kanagawa"] = "神奈川", ["Hokkaido"] = "北海道", ["Okinawa"] = "冲绳", ["Nagano"] = "长野",
        ["Aichi"] = "爱知", ["Fukuoka"] = "福冈", ["Chiba"] = "千叶",
        // Countries fallback
        ["China"] = "中国", ["Japan"] = "日本", ["Norway"] = "挪威", ["Switzerland"] = "瑞士",
        ["Thailand"] = "泰国", ["United Arab Emirates"] = "阿联酋", ["France"] = "法国",
        ["Italy"] = "意大利", ["Germany"] = "德国", ["United Kingdom"] = "英国",
        ["United States"] = "美国", ["Iceland"] = "冰岛", ["Spain"] = "西班牙",
        ["Portugal"] = "葡萄牙", ["Austria"] = "奥地利", ["Netherlands"] = "荷兰",
        ["Belgium"] = "比利时", ["Sweden"] = "瑞典", ["Finland"] = "芬兰", ["Denmark"] = "丹麦",
        ["Greece"] = "希腊", ["Turkey"] = "土耳其", ["South Korea"] = "韩国",
        ["Singapore"] = "新加坡", ["Malaysia"] = "马来西亚", ["Indonesia"] = "印度尼西亚",
        ["Vietnam"] = "越南", ["Australia"] = "澳大利亚", ["New Zealand"] = "新西兰",
        ["Canada"] = "加拿大", ["Czechia"] = "捷克", ["Hungary"] = "匈牙利",
    };

    public static PhotoMetadata? FromAssetJson(JsonElement item)
    {
        string? localDate = ReadString(item, "localDateTime");
        string? origDate = null;
        string? city = null;
        string? state = null;
        string? country = null;
        string? make = null;
        string? model = null;
        double? lat = null;
        double? lon = null;

        if (item.TryGetProperty("exifInfo", out var exif) && exif.ValueKind == JsonValueKind.Object)
        {
            origDate = ReadString(exif, "dateTimeOriginal");
            city = ReadString(exif, "city");
            state = ReadString(exif, "state");
            country = ReadString(exif, "country");
            make = ReadString(exif, "make");
            model = ReadString(exif, "model");
            lat = ReadDouble(exif, "latitude");
            lon = ReadDouble(exif, "longitude");
        }

        // Only trust localDateTime if EXIF original date exists, OR use localDateTime directly if present.
        var date = FormatDate(localDate) ?? FormatDate(origDate);
        var resolvedCity = FormatCity(country, state, city, lat, lon);
        var device = FormatDevice(make, model);
        var meta = new PhotoMetadata(date, resolvedCity, device);
        return meta.IsEmpty ? null : meta;
    }

    public static string? FormatDate(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw) || raw.Length < 10)
            return null;
        var span = raw.AsSpan().Trim();
        if (span.Length < 10 || span[4] != '-' || span[7] != '-')
            return null;
        if (!int.TryParse(span[..4], NumberStyles.None, CultureInfo.InvariantCulture, out var year) || year < 1990 || year > 2100)
            return null;
        if (!int.TryParse(span.Slice(5, 2), NumberStyles.None, CultureInfo.InvariantCulture, out var month) || month is < 1 or > 12)
            return null;
        if (!int.TryParse(span.Slice(8, 2), NumberStyles.None, CultureInfo.InvariantCulture, out var day) || day is < 1 or > 31)
            return null;
        return string.Create(CultureInfo.InvariantCulture, $"{year:0000}.{month:00}.{day:00}");
    }

    public static string? FormatCity(string? country, string? state, string? city, double? lat, double? lon)
    {
        // 1. GPS nearest major city (normalizes sub-districts and towns to prefecture-level Chinese city names)
        if (lat is double la && lon is double lo && !(Math.Abs(la) < 0.001 && Math.Abs(lo) < 0.001))
        {
            string? best = null;
            var bestDist = double.MaxValue;
            foreach (var c in KnownGeoCities)
            {
                var d = HaversineKm(la, lo, c.Lat, c.Lon);
                if (d <= c.RadiusKm && d < bestDist)
                {
                    bestDist = d;
                    best = c.Name;
                }
            }
            if (best != null)
                return best;
        }

        // 2. Direct Chinese / English city mapping
        if (!string.IsNullOrWhiteSpace(city))
        {
            var c = city.Trim();
            if (ContainsCjk(c))
                return TrimCitySuffix(c);
            if (PlaceMap.TryGetValue(c, out var mapped))
                return mapped;
        }

        // 3. State / prefecture mapping (e.g. Tokyo / Osaka / Hubei)
        if (!string.IsNullOrWhiteSpace(state))
        {
            var st = state.Trim();
            if (ContainsCjk(st))
                return TrimCitySuffix(st);
            if (PlaceMap.TryGetValue(st, out var mappedCity))
                return mappedCity;
            if (RegionMap.TryGetValue(st, out var mappedRegion))
                return mappedRegion;
        }

        // 4. Country fallback if city/state unavailable or unmapped
        if (!string.IsNullOrWhiteSpace(country))
        {
            var co = country.Trim();
            if (ContainsCjk(co))
                return co;
            if (RegionMap.TryGetValue(co, out var mappedCountry))
                return mappedCountry;
        }

        return string.IsNullOrWhiteSpace(city) ? null : city.Trim();
    }

    public static string? FormatDevice(string? make, string? model)
    {
        var mk = CleanText(make);
        var md = CleanText(model);
        if (md == null && mk == null)
            return null;
        if (md == null)
            return NormalizeBrand(mk!);

        // Apple iPhone / iPad already carry clean names
        if (md.StartsWith("iPhone", StringComparison.OrdinalIgnoreCase)
            || md.StartsWith("iPad", StringComparison.OrdinalIgnoreCase))
            return md;

        // Sony Alpha / ZV / RX model code prettification
        if (md.StartsWith("ILCE-", StringComparison.OrdinalIgnoreCase))
        {
            var code = md[5..].ToUpperInvariant();
            if (SonyIlce7Regex().Match(code) is { Success: true } m7)
            {
                var series = m7.Groups[1].Value; // "", "R", "S"
                var gen = m7.Groups[2].Value;    // "2", "3", "4", "5"
                var suffix = m7.Groups[3].Value; // optional "A" etc.
                return string.IsNullOrEmpty(series)
                    ? $"Sony A7M{gen}{suffix}"
                    : $"Sony A7{series}{gen}{suffix}";
            }
            if (code.StartsWith("7C", StringComparison.Ordinal))
                return "Sony A" + code;
            if (code is "1" or "1M2" || code.StartsWith("9", StringComparison.Ordinal) || code.Length == 4 && char.IsDigit(code[0]))
                return "Sony A" + code;
            return "Sony " + code;
        }
        if (md.StartsWith("DSC-RX", StringComparison.OrdinalIgnoreCase))
            return "Sony " + md[4..];
        if (md.StartsWith("ZV-", StringComparison.OrdinalIgnoreCase))
            return "Sony " + md;

        // Nikon Z6_2 -> Nikon Z6 II
        if (md.StartsWith("NIKON ", StringComparison.OrdinalIgnoreCase))
            md = md[6..].Trim();
        if (mk != null && mk.Contains("NIKON", StringComparison.OrdinalIgnoreCase))
        {
            md = md.Replace("_2", " II", StringComparison.Ordinal)
                   .Replace("_3", " III", StringComparison.Ordinal);
            return "Nikon " + md;
        }

        // Canon / Fujifilm / Ricoh / Leica / Hasselblad / DJI / Insta360
        var brand = mk != null ? NormalizeBrand(mk) : null;
        if (brand == null)
            return md;
        if (md.StartsWith(brand, StringComparison.OrdinalIgnoreCase))
            return brand + md[brand.Length..];
        return brand + " " + md;
    }

    static string NormalizeBrand(string raw)
    {
        var upper = raw.Trim().ToUpperInvariant();
        if (upper.Contains("SONY")) return "Sony";
        if (upper.Contains("FUJIFILM") || upper == "FUJI") return "Fujifilm";
        if (upper.Contains("CANON")) return "Canon";
        if (upper.Contains("NIKON")) return "Nikon";
        if (upper.Contains("RICOH")) return "Ricoh";
        if (upper.Contains("LEICA")) return "Leica";
        if (upper.Contains("HASSELBLAD")) return "Hasselblad";
        if (upper.Contains("PANASONIC") || upper.Contains("LUMIX")) return "Lumix";
        if (upper.Contains("OLYMPUS") || upper.Contains("OM DIGITAL")) return "OM System";
        if (upper.Contains("APPLE")) return "Apple";
        if (upper.Contains("DJI")) return "DJI";
        if (upper.Contains("INSTA360")) return "Insta360";
        if (upper.Contains("GOPRO")) return "GoPro";
        if (upper.Contains("HUAWEI")) return "Huawei";
        if (upper.Contains("XIAOMI")) return "Xiaomi";
        return raw.Trim();
    }

    static string TrimCitySuffix(string name)
        => name.Length > 2 && (name.EndsWith('市') || name.EndsWith('省')) ? name[..^1] : name;

    static bool ContainsCjk(string s)
    {
        foreach (var ch in s)
            if (ch is >= '\u4E00' and <= '\u9FFF')
                return true;
        return false;
    }

    static double HaversineKm(double lat1, double lon1, double lat2, double lon2)
    {
        const double r = 6371.0;
        var dLat = (lat2 - lat1) * Math.PI / 180.0;
        var dLon = (lon2 - lon1) * Math.PI / 180.0;
        var a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2)
            + Math.Cos(lat1 * Math.PI / 180.0) * Math.Cos(lat2 * Math.PI / 180.0)
            * Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
        return 2 * r * Math.Asin(Math.Min(1.0, Math.Sqrt(a)));
    }

    static string? CleanText(string? s)
        => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    static string? ReadString(JsonElement obj, string prop)
        => obj.TryGetProperty(prop, out var el) && el.ValueKind == JsonValueKind.String ? CleanText(el.GetString()) : null;

    static double? ReadDouble(JsonElement obj, string prop)
        => obj.TryGetProperty(prop, out var el) && el.ValueKind == JsonValueKind.Number && el.TryGetDouble(out var v) ? v : null;

    [GeneratedRegex(@"^7([RS]?)M(\d+)([A-Z]?)$")]
    private static partial Regex SonyIlce7Regex();
}
