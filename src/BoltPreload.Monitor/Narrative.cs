using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

/// <summary>
/// 把已经汇总好的业主材料写成一段话。句式固定，数字照材料里的写，不外发。
/// </summary>
internal static class Narrative
{
    private static readonly string[] Words =
    {
        "通道", "标定", "回波", "声时", "置零", "纳秒", "探头", "增益",
    };

    private static readonly Regex Token = new(@"(?i)(?<![a-z])(?:ch|ucf|ns)(?![a-z])", RegexOptions.Compiled);

    internal static bool HasBanned(string text)
    {
        foreach (var word in Words)
        {
            if (text.Contains(word, StringComparison.Ordinal)) return true;
        }

        return Token.IsMatch(text);
    }

    internal static string Speak(string briefJson)
    {
        using var doc = JsonDocument.Parse(briefJson);
        var root = doc.RootElement;
        var now = root.GetProperty("now");
        var range = root.GetProperty("rangeLabel").GetString() ?? "";
        var forceWord = root.GetProperty("forceWord").GetString() ?? "预紧力";
        var sb = new StringBuilder();

        sb.Append(CultureInfo.InvariantCulture, $"{range}监测 {now.GetProperty("bolts").GetInt32()} 颗，正常 {now.GetProperty("ok").GetInt32()} 颗，偏低 {now.GetProperty("low").GetInt32()} 颗，松动 {now.GetProperty("loose").GetInt32()} 颗，需要现场复核 {now.GetProperty("review").GetInt32()} 颗");
        AppendIfAny(sb, now, "unjudged", "还不能判断松紧");
        AppendIfAny(sb, now, "offline", "暂时没有新数据");
        if (now.TryGetProperty("healthPct", out var health) && health.ValueKind == JsonValueKind.Number)
        {
            sb.Append(CultureInfo.InvariantCulture, $"，健康率 {health.GetInt32()}%");
        }

        sb.Append('。');

        if (root.TryGetProperty("attention", out var attention)
            && attention.ValueKind == JsonValueKind.Array
            && attention.GetArrayLength() > 0)
        {
            var first = true;
            foreach (var item in attention.EnumerateArray())
            {
                if (!first) sb.Append('；');
                first = false;
                AppendItem(sb, item, forceWord);
            }

            sb.Append('。');
            if (root.TryGetProperty("attentionMore", out var more)
                && more.ValueKind == JsonValueKind.Number
                && more.GetInt32() > 0)
            {
                sb.Append(CultureInfo.InvariantCulture, $"另有 {more.GetInt32()} 颗没有逐一点名。");
            }
        }

        var period = root.GetProperty("period");
        sb.Append(CultureInfo.InvariantCulture, $"{range}新出现松动 {period.GetProperty("becameLoose").GetInt64()} 次，恢复正常 {period.GetProperty("recovered").GetInt64()} 次，读数基准更新 {period.GetProperty("readingReset").GetInt64()} 次，需要复核 {period.GetProperty("needsReview").GetInt64()} 次。");
        if (now.GetProperty("review").GetInt32() > 0) sb.Append("需要现场复核。");
        return sb.ToString();
    }

    private static void AppendIfAny(StringBuilder sb, JsonElement now, string name, string word)
    {
        if (!now.TryGetProperty(name, out var n) || n.ValueKind != JsonValueKind.Number || n.GetInt32() <= 0) return;
        sb.Append(CultureInfo.InvariantCulture, $"，{word} {n.GetInt32()} 颗");
    }

    private static void AppendItem(StringBuilder sb, JsonElement item, string forceWord)
    {
        sb.Append(item.GetProperty("place").GetString());
        sb.Append(' ');
        sb.Append(item.GetProperty("status").GetString());
        if (TryDecimal(item, "forceKn", out var force))
        {
            sb.Append(CultureInfo.InvariantCulture, $"，{forceWord} {Num(force)} kN");
        }
        else if (TryDecimal(item, "changeKn", out var change))
        {
            sb.Append(CultureInfo.InvariantCulture, $"，较安装时变化 {Num(change)} kN");
        }

        if (TryDecimal(item, "ratedKn", out var rated))
        {
            sb.Append(CultureInfo.InvariantCulture, $"，额定 {Num(rated)} kN");
        }

        if (item.TryGetProperty("ratioPct", out var ratio) && ratio.ValueKind == JsonValueKind.Number)
        {
            sb.Append(CultureInfo.InvariantCulture, $"，占 {ratio.GetInt32()}%");
        }

        if (!item.TryGetProperty("period", out var span) || span.ValueKind != JsonValueKind.Object) return;
        if (TryDecimal(span, "lowKn", out var low))
        {
            sb.Append(CultureInfo.InvariantCulture, $"，最低 {Num(low)} kN");
        }

        if (TryDecimal(span, "highKn", out var high))
        {
            sb.Append(CultureInfo.InvariantCulture, $"，最高 {Num(high)} kN");
        }
    }

    private static bool TryDecimal(JsonElement el, string name, out decimal value)
    {
        value = 0;
        if (!el.TryGetProperty(name, out var prop) || prop.ValueKind != JsonValueKind.Number) return false;
        value = prop.GetDecimal();
        return true;
    }

    private static string Num(decimal n) =>
        decimal.Round(n, 2, MidpointRounding.AwayFromZero).ToString("0.##", CultureInfo.InvariantCulture);
}
