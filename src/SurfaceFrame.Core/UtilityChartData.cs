using System.Globalization;
using System.Text.Json;

namespace SurfaceFrame;

public sealed record DailyCostPoint(
    string DateKey,
    string DayLabel,
    double ElecCost,
    double GasCost,
    double ElecKwh,
    double GasM3);

public sealed record UtilityMonthSummary(
    IReadOnlyList<DailyCostPoint> Days,
    double PeakTotalCost,
    double Elec7dKwh,
    double Gas7dM3);

public static class UtilityChartData
{
    public static UtilityMonthSummary ParseMonth(
        string? elecDailyCostJson,
        string? gasDailyCostJson,
        string? elecDailyUsageJson,
        string? gasDailyUsageJson)
    {
        var elecCost = ParseDailyMap(elecDailyCostJson);
        var gasCost = ParseDailyMap(gasDailyCostJson);
        var elecUsage = ParseDailyMap(elecDailyUsageJson);
        var gasUsage = ParseDailyMap(gasDailyUsageJson);

        var allKeys = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var k in elecCost.Keys) allKeys.Add(k);
        foreach (var k in gasCost.Keys) allKeys.Add(k);
        foreach (var k in elecUsage.Keys) allKeys.Add(k);
        foreach (var k in gasUsage.Keys) allKeys.Add(k);

        var points = new List<DailyCostPoint>(allKeys.Count);
        var peak = 0.0;
        foreach (var key in allKeys)
        {
            var eCost = elecCost.TryGetValue(key, out var ec) ? Math.Max(0, ec) : 0;
            var gCost = gasCost.TryGetValue(key, out var gc) ? Math.Max(0, gc) : 0;
            var eUse = elecUsage.TryGetValue(key, out var eu) ? Math.Max(0, eu) : 0;
            var gUse = gasUsage.TryGetValue(key, out var gu) ? Math.Max(0, gu) : 0;
            var label = key.Length >= 10 ? key[8..10].TrimStart('0') : key;
            if (string.IsNullOrEmpty(label))
                label = "0";
            points.Add(new DailyCostPoint(key, label, eCost, gCost, eUse, gUse));
            if (eCost + gCost > peak)
                peak = eCost + gCost;
        }

        double elec7d = 0;
        double gas7d = 0;
        var start7 = Math.Max(0, points.Count - 7);
        for (var i = start7; i < points.Count; i++)
        {
            elec7d += points[i].ElecKwh;
            gas7d += points[i].GasM3;
        }

        return new UtilityMonthSummary(
            points,
            peak > 0.01 ? peak * 1.12 : 10.0,
            elec7d,
            gas7d);
    }

    static Dictionary<string, double> ParseDailyMap(string? json)
    {
        var map = new Dictionary<string, double>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(json))
            return map;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return map;
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                if (prop.Value.ValueKind == JsonValueKind.Number && prop.Value.TryGetDouble(out var n))
                    map[prop.Name] = n;
                else if (prop.Value.ValueKind == JsonValueKind.String &&
                         double.TryParse(prop.Value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
                    map[prop.Name] = parsed;
            }
        }
        catch (JsonException)
        {
        }
        return map;
    }
}
