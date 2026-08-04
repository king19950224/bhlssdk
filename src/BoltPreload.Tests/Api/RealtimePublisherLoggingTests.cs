using BoltPreload.Api.Hubs;
using BoltPreload.Application.Alarm;
using BoltPreload.Application.Ingestion;
using BoltPreload.Fx.Models;
using FluentAssertions;
using Microsoft.AspNetCore.SignalR;
using Xunit;

namespace BoltPreload.Tests.Api;

/// <summary>
/// 三个 SignalR 推送实现在发送失败时必须记 Warning。
/// 之前都记的是 Debug，默认级别下等于没记；有人写过带节流的 Warning，但写在了
/// <see cref="IngestionService"/> 里，而实现按契约不抛异常，那个 catch 执行不到。
/// 所以留痕只能由实现层自己做，级别就是需求，靠断言钉住：改回 Debug 这里就红。
/// </summary>
public class RealtimePublisherLoggingTests
{
    [Fact]
    public async Task AlarmPublisher_LogsWarning_WhenPushFails()
    {
        var logger = new CapturingLogger<SignalRAlarmPublisher>();
        var publisher = new SignalRAlarmPublisher(new ThrowingHubContext(), logger);

        // 契约要求失败不抛：抛出去会反压入库主链
        await publisher.PublishAsync(Alarm(boltId: 42));

        var warnings = logger.Warnings(contains: "告警实时推送失败");
        warnings.Should().HaveCount(1, "告警推丢要记 Warning；记 Debug 默认级别下看不到");
        warnings[0].Should().Contain("bolt=42").And.Contain("preload_low",
            "得能看出是哪颗螺栓的哪类告警丢了");
    }

    [Fact]
    public async Task AlarmPublisher_DoesNotThrow_SoIngestionIsNeverBlocked()
    {
        var publisher = new SignalRAlarmPublisher(
            new ThrowingHubContext(), new CapturingLogger<SignalRAlarmPublisher>());

        await publisher.Invoking(p => p.PublishAsync(Alarm(boltId: 1)))
            .Should().NotThrowAsync("推送失败不能反压入库，留痕靠日志不靠异常");
    }

    [Fact]
    public async Task PresencePublisher_LogsWarning_WhenPushFails()
    {
        var logger = new CapturingLogger<SignalRChannelPresencePublisher>();
        var publisher = new SignalRChannelPresencePublisher(new ThrowingHubContext(), logger);

        var change = new ChannelPresenceChange(
            DeviceId: 7, ChannelNo: 3, Present: true, At: DateTimeOffset.UtcNow, Initial: false);
        await publisher.PublishAsync(change, channelId: 300, boltId: 900, boltCode: "B-3");

        logger.Warnings(contains: "在位事件推送失败")
            .Should().ContainSingle("插拔提示推丢了要留痕，不然现场以为探头没插上")
            .Which.Should().Contain("device=7").And.Contain("ch=3");
    }

    [Fact]
    public async Task MeasurementPublisher_LogsWarning_WhenPumpSendFails()
    {
        var logger = new CapturingLogger<SignalRMeasurementPublisher>();
        using var publisher = new SignalRMeasurementPublisher(new ThrowingHubContext(), logger);

        // PublishAsync 只入队；真正的发送失败出在后台泵任务里，所以要等它跑一轮
        await publisher.PublishAsync(Measurement(deviceId: 5));

        var warnings = await Eventually(() => logger.Warnings(contains: "实时推送失败"));
        warnings.Should().NotBeEmpty("泵里发送失败要记 Warning，不然大屏冻在旧数字上也查不到原因");
        warnings[0].Should().Contain("device=5");
    }

    [Fact]
    public async Task MeasurementPublisher_ThrottlesWarnings_SoAFloodCannotDrownTheLog()
    {
        var logger = new CapturingLogger<SignalRMeasurementPublisher>();
        using var publisher = new SignalRMeasurementPublisher(new ThrowingHubContext(), logger);

        // 50Hz × N 通道下推送要是一直失败，不节流会把日志刷爆
        for (var i = 0; i < 200; i++)
            await publisher.PublishAsync(Measurement(deviceId: 5));

        await Eventually(() => logger.Warnings(contains: "实时推送失败"));
        logger.Warnings(contains: "实时推送失败").Should().HaveCount(1,
            "节流窗口 1 秒，这一批只该落一条");
    }

    // ---- helpers ----

    /// <summary>等后台泵处理完（有界轮询，避免固定 sleep 造成的偶发红灯）。</summary>
    private static async Task<List<string>> Eventually(Func<List<string>> probe)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            var hit = probe();
            if (hit.Count > 0) return hit;
            await Task.Delay(20);
        }
        return probe();
    }

    private static AlarmEvent Alarm(long boltId) => new(
        Action: AlarmAction.Trigger, BoltId: boltId, ChannelId: 1,
        Type: "preload_low", Severity: "warn", Ts: DateTimeOffset.UtcNow,
        PreloadN: 100m, PreloadRatio: 0.5m, TargetPreloadN: 200m,
        ThresholdLowRatio: 0.8m, ThresholdHighRatio: 1.1m,
        ConsecutiveCount: 3, Message: "预紧力低于下限");

    private static FxDecodedEnvelope Measurement(long deviceId)
    {
        var cr = new FxChannelResultDto(
            Address: 1, Channel: 1, StatusCode: 0,
            EchoTimeNs: 1000, EchoTime2Ns: null, SpTempC: 25m, DeltaTofNs: null,
            Gain: 0f, EchoWave: null, LowAi: null, HighAi: null, Angle: null, MidResults: null);
        var ts = DateTimeOffset.UtcNow;
        return new FxDecodedEnvelope(deviceId, ts, ts, FxResultType.ChannelResult, ChannelResult: cr);
    }

    /// <summary>模拟客户端断线：任何发送都抛。</summary>
    private sealed class ThrowingHubContext : IHubContext<MeasurementsHub>
    {
        public IHubClients Clients { get; } = new ThrowingClients();
        public IGroupManager Groups => throw new NotSupportedException();

        private sealed class ThrowingClients : IHubClients
        {
            private static readonly IClientProxy Proxy = new ThrowingProxy();

            public IClientProxy All => Proxy;
            public IClientProxy AllExcept(IReadOnlyList<string> excludedConnectionIds) => Proxy;
            public IClientProxy Client(string connectionId) => Proxy;
            public IClientProxy Clients(IReadOnlyList<string> connectionIds) => Proxy;
            public IClientProxy Group(string groupName) => Proxy;
            public IClientProxy GroupExcept(string groupName, IReadOnlyList<string> excludedConnectionIds) => Proxy;
            public IClientProxy Groups(IReadOnlyList<string> groupNames) => Proxy;
            public IClientProxy User(string userId) => Proxy;
            public IClientProxy Users(IReadOnlyList<string> userIds) => Proxy;
        }

        private sealed class ThrowingProxy : IClientProxy
        {
            public Task SendCoreAsync(string method, object?[] args, CancellationToken cancellationToken = default)
                => throw new InvalidOperationException("模拟 SignalR 发送失败（客户端断线）");
        }
    }
}
