namespace BoltPreload.Domain;

/// <summary>
/// 螺栓国标标准值代码表（material-library-design §1）。
/// <para>
/// 性能等级 → Rp0.2 与 螺纹规格 → 应力截面积是<b>标准定义</b>（GB/T 3098.1 / GB/T 16823.1），
/// 不是现场数据——刻意做成代码常量而不进库：允许在库里维护它们，等于允许造出
/// "自定义的 8.8 级"，规格错误会顺着 GB 应力门直接变成预紧力误判。
/// 非标件走 bolt_spec_presets 的覆盖字段（cross_section_mm2 / rp02_mpa 显式给值）。
/// </para>
/// </summary>
public static class BoltStandards
{
    /// <summary>
    /// 性能等级 → Rp0.2（MPa，GB/T 3098.1-2010 表 3；不锈钢按 GB/T 3098.6 的 0.2% 非比例延伸强度）。
    /// 8.8 级分档：d ≤ 16mm 为 640，d &gt; 16mm 为 660（其余等级不分档）。
    /// 未收录等级返回 null——调用方必须显式让人填，不猜。
    /// </summary>
    public static decimal? Rp02Mpa(string grade, decimal nominalDiamMm)
    {
        var g = grade.Trim().ToUpperInvariant();
        return g switch
        {
            "4.6" => 240m,
            "4.8" => 340m,
            "5.6" => 300m,
            "5.8" => 420m,
            "6.8" => 480m,
            "8.8" => nominalDiamMm > 16m ? 660m : 640m,
            "9.8" => 720m,
            "10.9" => 940m,
            "12.9" => 1080m,
            "A2-70" or "A4-70" => 450m,
            "A2-80" or "A4-80" => 600m,
            _ => null,
        };
    }

    /// <summary>
    /// 粗牙普通螺纹公称直径（mm）→ 应力截面积 As（mm²，GB/T 16823.1）。
    /// 未收录规格返回 null。细牙/非标件用预设覆盖。
    /// </summary>
    public static decimal? StressAreaMm2(decimal nominalDiamMm) => nominalDiamMm switch
    {
        8m => 36.6m,
        10m => 58.0m,
        12m => 84.3m,
        14m => 115m,
        16m => 157m,
        18m => 192m,
        20m => 245m,
        22m => 303m,
        24m => 353m,
        27m => 459m,
        30m => 561m,
        33m => 694m,
        36m => 817m,
        39m => 976m,
        42m => 1120m,
        45m => 1310m,
        48m => 1470m,
        52m => 1760m,
        56m => 2030m,
        64m => 2680m,
        _ => null,
    };

    /// <summary>把 "M20"/"m20 "/"20" 解析为公称直径 mm；解析不出返回 null。</summary>
    public static decimal? ParseNominalDiamMm(string? nominalDiam)
    {
        if (string.IsNullOrWhiteSpace(nominalDiam)) return null;
        var digits = new string(nominalDiam.Where(c => char.IsDigit(c) || c == '.').ToArray());
        return decimal.TryParse(digits, System.Globalization.NumberStyles.Any,
            System.Globalization.CultureInfo.InvariantCulture, out var v) && v > 0
            ? v
            : null;
    }
}
