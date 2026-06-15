using BoltPreload.Fx;
using BoltPreload.Fx.Models;
using BoltPreload.Fx.Simulator;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BoltPreload.Tests.Fx;

public class SimulatedFxConnectorTests
{
    [Fact]
    public async Task Simulator_ProducesChannelResults_WithExpectedFields()
    {
        var options = new SimulatorOptions(
            DeviceId: 42,
            Address: 1,
            ChannelCount: 3,
            SamplingRateHz: 100);   // 100 Hz 测试用，跑得快一点

        await using var sut = new SimulatedFxConnector(options, NullLogger<SimulatedFxConnector>.Instance);

        await sut.ConnectAsync();
        sut.State.Should().Be(FxConnectorState.Connected);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var results = new List<FxDecodedEnvelope>();

        try
        {
            await foreach (var env in sut.Results.ReadAllAsync(cts.Token))
            {
                results.Add(env);
                if (results.Count >= 30) break;  // 收 30 条就够，验证 3 通道 × 10 轮
            }
        }
        catch (OperationCanceledException) { /* 超时正常 */ }

        await sut.DisconnectAsync();

        // 至少收到一些数据（100Hz × 3 通道 × 2 秒 = 600 条，取 30 没问题）
        results.Should().HaveCountGreaterThan(10);

        // 全部是 ChannelResult 类型
        results.Should().AllSatisfy(env =>
        {
            env.ResultType.Should().Be(FxResultType.ChannelResult);
            env.ChannelResult.Should().NotBeNull();
            env.ChannelResult!.StatusCode.Should().Be(0, "模拟器只产生正常数据");
            env.ChannelResult.EchoTimeNs.Should().BeGreaterThan(0);
            env.ChannelResult.SpTempC.Should().BeInRange(20m, 24m, "模拟温度 22 ± 1°C");
            env.ChannelResult.Address.Should().Be((ushort)1);
            env.ChannelResult.Channel.Should().BeInRange((ushort)1, (ushort)3);
        });

        // 3 个通道都有数据
        var channels = results.Select(r => r.ChannelResult!.Channel).Distinct().ToList();
        channels.Should().Contain(new ushort[] { 1, 2, 3 });

        // 设备 ID 正确
        results.Should().AllSatisfy(env => env.DeviceId.Should().Be(42));
    }
}
