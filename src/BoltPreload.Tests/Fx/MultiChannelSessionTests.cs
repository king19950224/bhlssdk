using BoltPreload.Fx;
using BoltPreload.Fx.Simulator;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BoltPreload.Tests.Fx;

public class MultiChannelSessionTests
{
    [Fact]
    public async Task Simulator_session_snapshot_contains_all_configured_channels()
    {
        var options = new SimulatorOptions(
            DeviceId: 1,
            Address: 1,
            ChannelCount: 40,
            SamplingRateHz: 1);

        await using var connector = new SimulatedFxConnector(options, NullLogger<SimulatedFxConnector>.Instance);

        var channels = connector.GetSessionChannelConfigs();

        channels.Should().HaveCount(40);
        channels.Select(x => x.Channel).Should().BeEquivalentTo(Enumerable.Range(1, 40).Select(x => (ushort)x));
        channels.Should().OnlyContain(x => x.ProbeFreqMhz > 0);
    }
}
