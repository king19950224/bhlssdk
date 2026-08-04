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
/// <para>
/// 这组用例针对的是一个"看起来已经修好了"的缺陷：曾经三个实现都记 Debug，默认级别下等于没记。
/// 有人发现过并写了带节流的 Warning，但写在了 <see cref="IngestionService"/> 里——
/// 而按接口约定实现不抛异常（不能让推送反压采集主链），那个 catch 永远轮不到，
/// 修复读起来完全正确却一行都不执行。同类的 Warning 在 PostgresMeasurementSink 里也躺着。
/// </para>
/// <para>
/// 因此可见性只能由实现层自己负责，而"记在哪个级别"就是需求本身，只能靠断言级别钉住：
/// 谁把这几行改回 Debug，下面就会红。
/// </para>
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
        warnings.Should().HaveCount(1,
            "告警推丢必须记 Warning。记 Debug 的话默认级别下什么都看不到，" +
            "而现场只盯大屏弹窗——告警落了库没弹窗，值班看到的就是“没有告警”");
        warnings[0].Should().Contain("bolt=42").And.Contain("preload_low",
            "日志要能定位到是哪颗螺栓的哪类告警丢了，否则事后无从查证");
    }

    [Fact]
    public async Task AlarmPublisher_DoesNotThrow_SoIngestionIsNeverBlocked()
    {
        var publisher = new SignalRAlarmPublisher(
            new ThrowingHubContext(), new CapturingLogger<SignalRAlarmPublisher>());

        await publisher.Invoking(p => p.PublishAsync(Alarm(boltId: 1)))
            .Should().NotThrowAsync("推送失败不能反压入库；可见性靠日志而不是异常");
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
            .Should().ContainSingle("探头插拔提示推丢了要留痕，否则现场以为探头没插上")
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
        warnings.Should().NotBeEmpty(
            "泵里发送失败必须记 Warning。这里静默的后果是大屏冻在旧数字上而日志一片安静");
        warnings[0].Should().Contain("device=5");
    }

    [Fact]
    public async Task MeasurementPublisher_ThrottlesWarnings_SoAFloodCannotDrownTheLog()
    {
        var logger = new CapturingLogger<SignalRMeasurementPublisher>();
        using var publisher = new SignalRMeasurementPublisher(new ThrowingHubContext(), logger);

        // 50Hz × N 通道下推送一旦持续失败，不节流会把日志刷爆
        for (var i = 0; i < 200; i++)
            await publisher.PublishAsync(Measurement(deviceId: 5));

        await Eventually(() => logger.Warnings(contains: "实时推送失败"));
        logger.Warnings(contains: "实时推送失败").Should().HaveCount(1,
            "节流窗口是 1 秒，这一批必须只落一条；每条都记会把业务日志淹掉");
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
