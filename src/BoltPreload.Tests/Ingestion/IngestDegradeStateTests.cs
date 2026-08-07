using BoltPreload.Application.Ingestion;
using FluentAssertions;
using Xunit;

namespace BoltPreload.Tests.Ingestion;

/// <summary>
/// 采集降级状态机。这台状态机是"DB 故障窗口内告警缺席"唯一的出声通道
/// （/ready 变 degraded + 恢复后补审计都靠它），转移错了就是假绿灯，按告警评估器同等强度钉住。
/// </summary>
public class IngestDegradeStateTests
{
    [Fact]
    public void 未达连续失败阈值不降级()
    {
        var s = new IngestDegradeState();

        s.ReportDbWriteFailure().Should().BeFalse();
        s.ReportDbWriteFailure().Should().BeFalse();

        s.DbWriteDegraded.Should().BeFalse("单条坏数据不该拉响全站降级");
        s.ConsecutiveDbWriteFailures.Should().Be(2);
    }

    [Fact]
    public void 连续三次失败进入降级且转移只报一次()
    {
        var s = new IngestDegradeState();

        s.ReportDbWriteFailure();
        s.ReportDbWriteFailure();
        s.ReportDbWriteFailure().Should().BeTrue("第三次失败触发 未降级→降级 转移");
        s.ReportDbWriteFailure().Should().BeFalse("已在降级态，不能重复报转移（避免 Critical 日志刷屏）");

        s.DbWriteDegraded.Should().BeTrue();
        s.DegradedSinceUtc.Should().NotBeNull();
    }

    [Fact]
    public void 中途一次成功打断连击()
    {
        var s = new IngestDegradeState();

        s.ReportDbWriteFailure();
        s.ReportDbWriteFailure();
        s.ReportDbWriteSuccess();
        s.ReportDbWriteFailure();
        s.ReportDbWriteFailure();

        s.DbWriteDegraded.Should().BeFalse("间歇性失败（每次都没连满三条）不算降级");
    }

    [Fact]
    public void 恢复产出窗口且失败计数只算窗口内()
    {
        var s = new IngestDegradeState();

        for (var i = 0; i < 5; i++) s.ReportDbWriteFailure();

        var w = s.ReportDbWriteSuccess();
        w.Should().NotBeNull();
        w!.FailureCount.Should().Be(5, "进入前的阈值三条也属于这轮故障，一并计入");
        w.From.Should().BeOnOrBefore(w.To);
        s.DbWriteDegraded.Should().BeFalse();

        s.ReportDbWriteSuccess().Should().BeNull("已恢复，再成功不产生新窗口");
    }

    [Fact]
    public void 待审计窗口取走即清_没取走时两轮合并不丢事实()
    {
        var s = new IngestDegradeState();

        // 第一轮降级恢复
        for (var i = 0; i < 3; i++) s.ReportDbWriteFailure();
        s.ReportDbWriteSuccess();
        // 审计还没补写，第二轮又来了
        for (var i = 0; i < 4; i++) s.ReportDbWriteFailure();
        s.ReportDbWriteSuccess();

        s.HasPendingRecoveredWindow.Should().BeTrue();
        s.TryTakeRecoveredWindow(out var merged).Should().BeTrue();
        merged.FailureCount.Should().Be(7, "两轮窗口合并后失败数是 3+4，不能丢");

        s.HasPendingRecoveredWindow.Should().BeFalse("单槽取走即清");
        s.TryTakeRecoveredWindow(out _).Should().BeFalse();
    }

    [Fact]
    public void WAL丢弃计入总数并在粘滞窗内算降级理由()
    {
        var s = new IngestDegradeState();

        s.WalDroppedRecently.Should().BeFalse();
        s.ReportWalDropped(120);
        s.ReportWalDropped(30);

        s.WalDroppedTotal.Should().Be(150);
        s.WalLastDropAtUtc.Should().NotBeNull();
        s.WalDroppedRecently.Should().BeTrue("刚丢完必须还在粘滞窗内，/ready 才有理由报 degraded");

        s.ReportWalDropped(0);
        s.WalDroppedTotal.Should().Be(150, "非正数不计");
    }

    [Fact]
    public void 恢复窗口计入窗口期内的WAL丢弃()
    {
        var s = new IngestDegradeState();

        s.ReportWalDropped(10);           // 降级前的丢弃不算进窗口
        for (var i = 0; i < 3; i++) s.ReportDbWriteFailure();
        s.ReportWalDropped(25);           // 窗口内丢弃

        var w = s.ReportDbWriteSuccess();
        w!.WalDropped.Should().Be(25, "审计里报的是这轮故障期间丢了多少，不是历史总量");
    }
}
