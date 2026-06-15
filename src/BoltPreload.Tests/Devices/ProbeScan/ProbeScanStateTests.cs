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
