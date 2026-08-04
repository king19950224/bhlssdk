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

    /// <summary>
    /// 把用户文本嵌进"整行不是标准字段"的位置（表头前的注释行、标题行）时用这个。
    /// <para>
    /// 这种位置不能靠 <see cref="Field"/>：CSV 的引号只在字段起始处才有转义含义，
    /// 嵌在 <c>"# 螺栓 …"</c> 中间的引号是字面量，值里的 CRLF 照样能把行截断。
    /// 而新起那一行的首字符完全由输入决定 —— 一个编号叫 <c>"A\r\n=HYPERLINK(…)"</c> 的螺栓，
    /// 导出的表在客户 Excel 里就多出一行公式并被执行。
    /// </para>
    /// <para>所以这里的要点是"压成一行"：所有控制字符（含 CR/LF/Tab）折成空格。</para>
    /// </summary>
    public static string Inline(string? value)
    {
        if (string.IsNullOrEmpty(value)) return "";
        return new string(value.Select(c => char.IsControl(c) ? ' ' : c).ToArray()).Trim();
    }

    /// <summary>
    /// 把用户文本嵌进下载文件名时用这个：控制字符会破坏 Content-Disposition 头，
    /// 路径分隔符会被浏览器当成目录。逐个换成下划线，口径与
    /// <see cref="ReportEndpoints.DownloadName(string?, string)"/> 一致。
    /// </summary>
    public static string FileNameSegment(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";
        return new string(value.Trim()
            .Select(c => char.IsControl(c) || c is '/' or '\\' or ':' or '*' or '?' or '"' or '<' or '>' or '|' ? '_' : c)
            .ToArray());
    }
}
