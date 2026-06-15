using BoltPreload.Application.Devices;
using FluentAssertions;
using Xunit;

namespace BoltPreload.Tests.Infrastructure;

public class ChannelConfigValuesTests
{
    [Fact]
    public void Channel_config_values_can_represent_desired_actual_and_effective_layers()
    {
        var desired = new ChannelConfigValues(
            ProbeFreqMhz: 2m,
            Gain: 16m,
            Threshold: -490,
            MeasMode: 0,
            RefTimeNs: 32142m,
            RefTempC: 16.71m,
            TcMode: 1,
            TempCoefficient: null,
            EchoWinNs: null,
            EchoWidthUs: 15,
            Source: "database");

        var actual = desired with
        {
            Threshold = 0,
            RefTimeNs = 32558m,
            RefTempC = 15.30m,
            Source = "device_readback"
        };

        actual.Source.Should().Be("device_readback");
        desired.RefTimeNs.Should().Be(32142m);
        actual.RefTimeNs.Should().Be(32558m);
    }
}
