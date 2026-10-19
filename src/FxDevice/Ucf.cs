using System.Text.Json;

namespace FxDevice;

public static class Ucf
{
    public sealed record Cal(decimal K, decimal B, string Unit, int Points, string FileName);

    // 没有 .ucf 就不算力值。不要写默认斜率。

    /// <summary>
    /// 伸长量换算（µm/ns）：厂家用同一试件标定得 25.81 µm / 31.99 ns 反推，含声弹性修正。
    /// 单试件的数，只作参考显示，不参与任何判定。
    /// </summary>
    public const decimal UmPerNs = 0.807m;

    public static Cal Load(string path)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var root = doc.RootElement;

        var xs = new List<double>();
        var ys = new List<double>();
        foreach (var key in new[] { "SelectedDatas", "AvgDatas", "Datas" })
        {
            if (!root.TryGetProperty(key, out var node)) continue;
            var arr = node.ValueKind == JsonValueKind.Object && node.TryGetProperty("v", out var inner)
                ? inner
                : node;
            if (arr.ValueKind != JsonValueKind.Array) continue;
            foreach (var p in arr.EnumerateArray())
            {
                if (p.ValueKind != JsonValueKind.Array || p.GetArrayLength() < 2) continue;
                xs.Add(p[0].GetDouble());
                ys.Add(p[1].GetDouble());
            }

            if (xs.Count >= 2) break;
            xs.Clear();
            ys.Clear();
        }

        if (xs.Count < 2) throw new InvalidOperationException("文件中没有标定点");

        var n = xs.Count;
        var sx = xs.Sum();
        var sy = ys.Sum();
        var sxx = xs.Sum(x => x * x);
        var sxy = xs.Zip(ys, (x, y) => x * y).Sum();
        var denom = n * sxx - sx * sx;
        if (Math.Abs(denom) < 1e-9) throw new InvalidOperationException("标定点无效，无法拟合");

        var k = (n * sxy - sx * sy) / denom;
        var b = (sy - k * sx) / n;
        if (k <= 0) throw new InvalidOperationException($"拟合斜率为 {k:F6}，不是正数");

        var unit = root.TryGetProperty("CalibUnit", out var u) ? u.GetString() ?? "kN" : "kN";
        return new Cal((decimal)k, Math.Abs(b) < 0.005 ? 0m : (decimal)b, unit, n, Path.GetFileName(path));
    }
}
