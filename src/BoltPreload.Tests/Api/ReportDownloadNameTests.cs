using BoltPreload.Api.Endpoints;
using FluentAssertions;
using Xunit;

namespace BoltPreload.Tests.Api;

/// <summary>
/// 报告下载名的取名规则。落盘用的是 flange-group-1-2026....html 这种 ASCII 名字，
/// 图的是文件系统和 URL 上不出岔子；客户点下载存下来的那份要是报告标题。
/// 标题是人填的，什么都可能打进去，这里盯住几个会出事的输入。
/// </summary>
public class ReportDownloadNameTests
{
    private const string Stored = "/data/reports/flange/flange-group-1-20260731090324.html";

    [Fact]
    public void 用报告标题当下载名并补上扩展名()
    {
        ReportEndpoints.DownloadName("1号风机 塔筒顶法兰 螺栓预紧力验收报告", Stored)
            .Should().Be("1号风机 塔筒顶法兰 螺栓预紧力验收报告.html");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("///")]   // 清干净后什么都不剩
    public void 标题不可用时退回存盘名(string? title)
    {
        ReportEndpoints.DownloadName(title, Stored)
            .Should().Be("flange-group-1-20260731090324.html");
    }

    [Fact]
    public void 标题里的路径分隔符要换掉()
    {
        // 带斜杠的名字塞进 Content-Disposition，浏览器会当成路径处理
        ReportEndpoints.DownloadName("A/B:C*D?E\"F<G>H|I", Stored)
            .Should().Be("A_B_C_D_E_F_G_H_I.html");
    }

    [Fact]
    public void 标题已经带扩展名就不再重复追加()
    {
        ReportEndpoints.DownloadName("验收报告.html", Stored).Should().Be("验收报告.html");
    }

    [Fact]
    public void 超长标题要截断否则有的系统存不下()
    {
        var name = ReportEndpoints.DownloadName(new string('长', 200), Stored);
        name.Should().Be(new string('长', 80) + ".html");
    }
}
