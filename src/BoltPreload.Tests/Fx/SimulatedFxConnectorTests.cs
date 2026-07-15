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

    [Fact]
    public async Task Simulator_ProbeUnplugAndReplug_TogglesEchoReporting()
    {
        var options = new SimulatorOptions(DeviceId: 42, Address: 1, ChannelCount: 2, SamplingRateHz: 100);
        await using var sut = new SimulatedFxConnector(options, NullLogger<SimulatedFxConnector>.Instance);

        // 拔出 ch2：仍随轮询上报，但无回波（模拟空口/探头脱落的真机表现）
        sut.SetProbePresent(2, present: false);
        await sut.ConnectAsync();

        var unplugged = await CollectAsync(sut, count: 40);
        unplugged.Where(r => r.Channel == 2).Should().NotBeEmpty("拔出的通道仍被设备轮询上报")
            .And.AllSatisfy(r =>
            {
                r.EchoTimeNs.Should().BeNull("空口无回波");
                r.StatusCode.Should().Be((short)-1, "信号过小");
            });
        unplugged.Where(r => r.Channel == 1).Should().NotBeEmpty()
            .And.AllSatisfy(r => r.EchoTimeNs.Should().NotBeNull("在位通道正常测量"));

        // 插回 ch2：恢复正常回波
        sut.SetProbePresent(2, present: true);
        // 清掉切换前积压的旧数据再采样
        while (sut.Results.TryRead(out _)) { }
        var replugged = await CollectAsync(sut, count: 40);
        replugged.Where(r => r.Channel == 2).Should().NotBeEmpty()
            .And.AllSatisfy(r => r.EchoTimeNs.Should().NotBeNull("插回后恢复回波"));

        await sut.DisconnectAsync();
    }

    private static async Task<List<FxChannelResultDto>> CollectAsync(SimulatedFxConnector sut, int count)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var list = new List<FxChannelResultDto>();
        try
        {
            await foreach (var env in sut.Results.ReadAllAsync(cts.Token))
            {
                if (env.ChannelResult is not null) list.Add(env.ChannelResult);
                if (list.Count >= count) break;
            }
        }
        catch (OperationCanceledException) { /* 超时 */ }
        return list;
    }
}
