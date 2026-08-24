using BoltPreload.Application.Devices.ProbeScan;
using FluentAssertions;
using Xunit;

namespace BoltPreload.Tests.Devices.ProbeScan;

public class ProbeScanStateTests
{
    [Fact]
    public void TryBegin_FirstTime_ReturnsHandle()
    {
        var state = new ProbeScanState();
        var handle = state.TryBegin(deviceId: 1, scanRunId: 42);
        handle.Should().NotBeNull();
        handle!.DeviceId.Should().Be(1);
        handle.ScanRunId.Should().Be(42);
        state.IsRunning(1).Should().BeTrue();
    }

    [Fact]
    public void TryBegin_SecondTime_SameDevice_ReturnsNull()
    {
        var state = new ProbeScanState();
        state.TryBegin(1, 42).Should().NotBeNull();
        state.TryBegin(1, 43).Should().BeNull();
        state.IsRunning(1).Should().BeTrue();
    }

    [Fact]
    public void TryBegin_DifferentDevices_BothSucceed()
    {
        var state = new ProbeScanState();
        state.TryBegin(1, 42).Should().NotBeNull();
        state.TryBegin(2, 99).Should().NotBeNull();
        state.IsRunning(1).Should().BeTrue();
        state.IsRunning(2).Should().BeTrue();
    }

    [Fact]
    public void LastEndedAt_SetOnEnd_UsedForPostScanGrace()
    {
        // 在位判定的"扫描后宽限"依赖它：扫完设备重新跑起全通道轮转前，误报拔出会冲掉扫描判定
        var state = new ProbeScanState();
        state.LastEndedAt(1).Should().BeNull("从未扫描过");

        state.TryBegin(1, 42).Should().NotBeNull();
        state.LastEndedAt(1).Should().BeNull("扫描进行中还没结束");

        var before = DateTimeOffset.UtcNow;
        state.End(1);
        state.LastEndedAt(1).Should().NotBeNull()
            .And.BeOnOrAfter(before);

        // 未占位的 End（如重复调用）不应刷新结束时刻
        var first = state.LastEndedAt(1);
        state.End(1);
        state.LastEndedAt(1).Should().Be(first);
    }

    [Fact]
    public void End_RemovesHandle()
    {
        var state = new ProbeScanState();
        state.TryBegin(1, 42);
        state.End(1);
        state.IsRunning(1).Should().BeFalse();
        state.TryBegin(1, 43).Should().NotBeNull();
    }

    [Fact]
    public void TryAbort_MatchingScanRunId_CancelsToken()
    {
        var state = new ProbeScanState();
        var handle = state.TryBegin(1, 42);
        handle.Should().NotBeNull();

        var ok = state.TryAbort(1, 42);
        ok.Should().BeTrue();
        handle!.Cts.IsCancellationRequested.Should().BeTrue();
    }

    [Fact]
    public void TryAbort_WrongScanRunId_ReturnsFalse()
    {
        var state = new ProbeScanState();
        state.TryBegin(1, 42);
        state.TryAbort(1, 99).Should().BeFalse();
    }

    [Fact]
    public void TryAbort_DeviceNotRunning_ReturnsFalse()
    {
        var state = new ProbeScanState();
        state.TryAbort(1, 42).Should().BeFalse();
    }
}
