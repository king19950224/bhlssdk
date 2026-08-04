namespace BoltPreload.Api.Endpoints;

/// <summary>
/// CSV 导出的字段转义，全项目唯一一份。
/// <para>
/// 收口之前是两套：审计日志那份做了公式注入防护并写明了理由，验收资料那份（还被点位表复用）
/// 只处理逗号和引号。两边导的都是带用户自由文本的客户交付文件，同一威胁模型下不该有两个标准 ——
/// 备注里一句 <c>=HYPERLINK(...)</c>，客户用 Excel 打开验收报告时就会执行。
/// </para>
/// </summary>
internal static class CsvExport
{
    /// <summary>
    /// 转义一个 CSV 字段。
    /// <para>
    /// 只用于文本列。数值和时间戳请直接拼接：给数字前置单引号会让 Excel 当成文本，
    /// 客户拿到的表就没法排序和求和了。
    /// </para>
    /// </summary>
    public static string Field(string? value)
    {
        if (string.IsNullOrEmpty(value)) return "";
        // 公式注入防护：以 = + - @ 开头的值被 Excel 当公式执行，前置单引号中和
        if (value[0] is '=' or '+' or '-' or '@') value = "'" + value;
        // 恒加引号并把内部引号翻倍，避免逗号/换行/引号破坏列
        return "\"" + value.Replace("\"", "\"\"") + "\"";
    }
}
