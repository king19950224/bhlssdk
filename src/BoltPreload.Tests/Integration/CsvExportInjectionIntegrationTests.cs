using System.Net;
using System.Text;
using FluentAssertions;
using NpgsqlTypes;
using Xunit;

namespace BoltPreload.Tests.Integration;

/// <summary>
/// 各 CSV 导出端点有没有真的接上转义。
/// <para>
/// <c>CsvExport</c> 三个方法各有单元测试，但端点有没有调用它是另一回事：趋势导出的注释行以前
/// 直接把螺栓编号插进去，点位表的规格与等级两列以前裸拼。漏接不影响功能，表照样打得开，
/// 只有拿到特定编号的那一份会在客户机器上执行公式。
/// </para>
/// <para>
/// 断言统一按"导出内容里没有任何一行以 Excel 公式起始符开头"来判，比逐处比对转义结果更贴近
/// 要防的事，格式微调也不会让它失效。
/// </para>
/// </summary>
[Collection("postgres")]
public sealed class CsvExportInjectionIntegrationTests
{
    private readonly PostgresFixture _fx;
    public CsvExportInjectionIntegrationTests(PostgresFixture fx) => _fx = fx;

    /// <summary>编号里藏一个 CRLF：目的是结束当前行，让后面的公式独占一行。</summary>
    private const string BreakoutCode = "B-1\r\n=HYPERLINK(\"http://evil\",\"发票\")";

    [PostgresFact]
    public async Task PreloadTrendCsv_CannotBeBrokenOutOfTheCommentLine()
    {
        _fx.EnsureAvailable();
        var seed = await _fx.SeedCalibratableChannelAsync("趋势导出注入");
        await SetBoltTextAsync(seed.BoltId, "code", BreakoutCode);

        using var factory = TestApi.CreateFactory(_fx);
        using var client = TestApi.AuthedClient(factory, await _fx.SeedUserAsync("reviewer"), "reviewer");

        var resp = await client.GetAsync($"/api/bolts/{seed.BoltId}/preload-trend/export.csv");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        var lines = await CsvLinesAsync(resp);
        lines.Should().NotContain(l => StartsFormula(l),
            "编号里的 CRLF 把注释行截断后，新起那行的首字符由输入决定，Excel 会当公式执行");
        lines[0].Should().StartWith("# 螺栓 B-1", "编号原文要留着，只是折成一行——不是把值删掉");
    }

    [PostgresFact]
    public async Task WiringTableCsv_EscapesSpecAndGradeColumns()
    {
        _fx.EnsureAvailable();
        var seed = await _fx.SeedCalibratableChannelAsync("点位表导出注入");
        // 规格与等级也是自由文本：未知规格允许调用方显式带 crossSectionMm2/rp02Mpa 通过建档校验
        await SetBoltTextAsync(seed.BoltId, "nominal_diam", "=1+1");
        await SetBoltTextAsync(seed.BoltId, "grade", "=1+2");

        using var factory = TestApi.CreateFactory(_fx);
        using var client = TestApi.AuthedClient(factory, await _fx.SeedUserAsync("reviewer"), "reviewer");

        var resp = await client.GetAsync($"/api/devices/{seed.DeviceId}/wiring-table/export.csv");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        var csv = string.Join("\n", await CsvLinesAsync(resp));
        csv.Should().Contain("\"'=1+1\"").And.Contain("\"'=1+2\"",
            "规格与等级两列要走 CsvExport.Field，公式前缀被单引号中和");
        csv.Should().NotContain(",=1+", "裸拼的话这两列就是能被 Excel 执行的公式");
    }

    [PostgresFact]
    public async Task WiringTableCsv_WrapsNewlineBearingTextInQuotes_WhichIsWhyItNeedsNoInline()
    {
        _fx.EnsureAvailable();
        var seed = await _fx.SeedCalibratableChannelAsync("点位表换行");
        // 位置标签是人填的，从别处粘贴时带进回车很常见，不必是攻击
        await SetBoltTextAsync(seed.BoltId, "position_label", "A\r\n=1+1");

        using var factory = TestApi.CreateFactory(_fx);
        using var client = TestApi.AuthedClient(factory, await _fx.SeedUserAsync("reviewer"), "reviewer");

        var resp = await client.GetAsync($"/api/devices/{seed.DeviceId}/wiring-table/export.csv");
        var csv = await CsvTextAsync(resp);

        // 标准列走 Field，值被整个包进引号。引号内的换行是合法 CSV：Excel 按一个单元格读，
        // 单元格值以 "A" 开头 → 文本，不是公式。所以这里【不需要】也不应该用 Inline，
        // 否则会把客户自己填的换行改掉。
        // 注意别照搬"任何一行都不能以 = 开头"那条断言：那条只对注释行成立，
        // 因为注释行不是标准字段、引号在那里不转义（见 PreloadTrendCsv 用例）。
        csv.Should().Contain("\"A\r\n=1+1\"", "换行要留在引号里，而不是被删掉或裸露成新行");
        csv.Should().NotContain(",A\r\n=1+1", "不包引号才是真出事：那样 =1+1 会独占一行成为公式");
    }

    // ---- helpers ----

    /// <summary>Excel 把以 = + - @ 开头的单元格当公式执行。</summary>
    private static bool StartsFormula(string line)
        => line.Length > 0 && line[0] is '=' or '+' or '-' or '@';

    /// <summary>
    /// 去掉 BOM 后按物理行切开（末尾空行丢掉）。
    /// <para>
    /// 这是"注释行有没有被截断"的视角，只适用于不带引号的注释行/标题行。
    /// 标准字段里引号内的换行也会在这里切成两行，但那对 Excel 仍是一个单元格 ——
    /// 对标准列请改用 <see cref="CsvTextAsync"/> 断言引号包裹。
    /// </para>
    /// </summary>
    private static async Task<string[]> CsvLinesAsync(HttpResponseMessage resp)
        => (await CsvTextAsync(resp))
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.TrimEnd('\r'))
            .ToArray();

    /// <summary>去掉 BOM 后的完整正文（保留原始换行）。</summary>
    private static async Task<string> CsvTextAsync(HttpResponseMessage resp)
    {
        var bytes = await resp.Content.ReadAsByteArrayAsync();
        var bom = Encoding.UTF8.GetPreamble();
        bytes.Take(bom.Length).Should().Equal(bom, "没有 BOM 的话 Excel 打开是一屏乱码");
        return Encoding.UTF8.GetString(bytes, bom.Length, bytes.Length - bom.Length);
    }

    private async Task SetBoltTextAsync(long boltId, string column, string value)
    {
        // column 是本类内部写死的列名，不来自外部输入
        await using var cmd = _fx.DataSource.CreateCommand($"UPDATE bolts SET {column} = @v WHERE id = @id");
        cmd.Parameters.AddWithValue("v", NpgsqlDbType.Varchar, value);
        cmd.Parameters.AddWithValue("id", NpgsqlDbType.Bigint, boltId);
        await cmd.ExecuteNonQueryAsync();
    }
}
