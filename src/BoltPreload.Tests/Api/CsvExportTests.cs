using BoltPreload.Api.Endpoints;
using FluentAssertions;
using Xunit;

namespace BoltPreload.Tests.Api;

/// <summary>
/// CsvExport 的转义与公式注入防护。
/// <para>
/// 验收资料、审计日志、点位表这三份 CSV 都是直接交给客户、由客户在 Excel 里打开的，
/// 而且都含用户自由填写的备注列。备注里写一句 <c>=HYPERLINK(...)</c> 或 <c>=cmd|...</c>，
/// Excel 打开时会当公式执行 —— 这条防线之前没有任何测试守着，谁顺手改一下没人发现。
/// </para>
/// <para>
/// <c>Field</c> 管标准字段；<c>Inline</c> 管表头前的注释行（那里引号不转义，只能靠折换行）；
/// <c>FileNameSegment</c> 管下载名。三处的威胁模型不同，不能互相替代。
/// </para>
/// </summary>
public class CsvExportTests
{
    [Theory]
    [InlineData("=HYPERLINK(\"http://evil\",\"发票\")")]
    [InlineData("=1+1")]
    [InlineData("+44 1234")]
    [InlineData("-2+3")]
    [InlineData("@SUM(A1:A9)")]
    public void Field_LeadingFormulaTrigger_IsNeutralizedWithApostrophe(string dangerous)
    {
        // Excel 把以 = + - @ 开头的单元格当公式。前置单引号后 Excel 视为文本，内容照常显示。
        var escaped = CsvExport.Field(dangerous);

        escaped.Should().StartWith("\"'", "危险前缀必须被单引号中和");
        escaped.Should().Contain(dangerous.Replace("\"", "\"\""), "中和之后原文要完整保留，不能被吃掉");
    }

    [Theory]
    [InlineData("正常备注")]
    [InlineData("M16 螺栓 8.8 级")]
    [InlineData("a=b")]
    [InlineData("温度 -5 ℃")]
    public void Field_NoLeadingTrigger_IsNotPrefixed(string safe)
    {
        // 只看首字符。等号出现在中间是正常文本，多加单引号反而污染客户看到的内容。
        CsvExport.Field(safe).Should().Be($"\"{safe}\"");
    }

    [Fact]
    public void Field_EmbeddedQuotes_AreDoubled()
    {
        // RFC 4180：字段内的引号写成两个，否则一句带引号的备注就会把整行的列切错位。
        CsvExport.Field("他说\"松了\"").Should().Be("\"他说\"\"松了\"\"\"");
    }

    [Fact]
    public void Field_CommaAndNewline_StayInsideOneColumn()
    {
        // 恒加外层引号的意义就在这：备注里的逗号和换行不能把一行拆成多列多行。
        CsvExport.Field("先复紧，再复测").Should().Be("\"先复紧，再复测\"");
        CsvExport.Field("第一行\n第二行").Should().Be("\"第一行\n第二行\"");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Field_NullOrEmpty_ProducesBareEmptyField(string? value)
    {
        // 空值输出空串而不是 ""，让 Excel 显示成真正的空单元格而不是一对可见引号。
        CsvExport.Field(value).Should().Be("");
    }

    [Fact]
    public void Field_NegativeNumberAsText_GetsPrefixed_WhichIsWhyNumbersMustNotUseIt()
    {
        // 记录这个已知取舍：-12.5 会被当成危险前缀加上单引号，Excel 里就成了文本，不能排序求和。
        // 所以数值列必须直接拼接、不走本方法（CsvExport.Field 的文档注释里写了这条约束）。
        CsvExport.Field("-12.5").Should().Be("\"'-12.5\"");
    }

    // ---- Inline：表头前的注释行 ----

    [Fact]
    public void Inline_NewlineInValue_CannotForgeAnExtraFormulaRow()
    {
        // 趋势导出的注释行长这样："# 螺栓 {code} 预紧力趋势导出 …"。
        // 编号里塞进 CRLF 就能结束该行、另起一行，而新行的首字符由输入决定 ——
        // 于是客户在 Excel 里打开时多出一行 =HYPERLINK(...) 并被执行。
        var attack = "B-1\r\n=HYPERLINK(\"http://evil\",\"发票\")";

        var line = $"# 螺栓 {CsvExport.Inline(attack)} 预紧力趋势导出";

        line.Should().NotContain("\n").And.NotContain("\r");
        line.Should().StartWith("# 螺栓 B-1", "编号原文要留着，只是折成一行");
    }

    [Fact]
    public void Inline_IsRequiredThere_BecauseFieldCannotEscapeMidLine()
    {
        // 记录这个反直觉的事实，免得以后有人"统一都改成 Field"把防护改没了：
        // CSV 的引号只在字段起始处才有转义含义，嵌在行中间的引号是字面量。
        var attack = "B-1\r\n=1+1";

        $"# 螺栓 {CsvExport.Field(attack)}".Should().Contain("\n",
            "Field 会把值包进引号，但行中间的引号不转义，CRLF 照样把行截断");
        $"# 螺栓 {CsvExport.Inline(attack)}".Should().NotContain("\n");
    }

    [Theory]
    [InlineData("A\tB", "A B")]
    [InlineData("A\u0000B", "A B")]
    [InlineData("  A  ", "A")]
    public void Inline_CollapsesControlCharsAndTrims(string input, string expected)
        => CsvExport.Inline(input).Should().Be(expected);

    // ---- FileNameSegment：下载名 ----

    [Fact]
    public void FileNameSegment_ControlChars_WouldOtherwiseBreakTheHeader()
    {
        // Content-Disposition 里出现裸 CRLF，Kestrel 写响应头时会直接抛，下载变 500
        CsvExport.FileNameSegment("B-1\r\n1").Should().Be("B-1__1");
    }

    [Fact]
    public void FileNameSegment_PathSeparators_AreReplaced()
    {
        CsvExport.FileNameSegment("A/B\\C:D").Should().Be("A_B_C_D",
            "带斜杠的名字浏览器会当成路径");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("   ")]
    public void FileNameSegment_NullOrBlank_ProducesEmpty(string? value)
        => CsvExport.FileNameSegment(value).Should().Be("");
}
