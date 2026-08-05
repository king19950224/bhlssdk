using BoltPreload.Application.Ingestion;
using BoltPreload.Fx.Models;
using BoltPreload.Infrastructure.Ingestion;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Npgsql;
using Xunit;

namespace BoltPreload.Tests.Ingestion;

/// <summary>
/// 未路由通道（未接探头 / 墓碑通道）丢弃日志的聚合/节流行为。
/// 真机一轮多通道扫描里可能有几十路空通道，旧实现逐通道刷 WARN 会淹没业务日志；
/// 现按【设备】聚合：每设备首次出现只记一条提示，之后周期汇总。这里验证"首次只记一条、按设备区分"。
/// 走公开的 <see cref="PostgresMeasurementSink.WriteAsync(FxDecodedEnvelope, System.Threading.CancellationToken)"/>，
/// 未路由分支只打日志、不碰 DB，因此无需真实数据库。
/// </summary>
public class PostgresMeasurementSinkLoggingTests
{
    [Fact]
    public async Task UnroutedChannels_OnOneDevice_LogsSingleAggregatedNotice()
    {
        var logger = new CapturingLogger<PostgresMeasurementSink>();
        await using var ds = NewUnusedDataSource();
        var sink = new PostgresMeasurementSink(ds, new UnroutedResolver(), logger);

        // 同一设备的 10 路通道全部无法路由
        for (ushort ch = 1; ch <= 10; ch++)
            await sink.WriteAsync(Unrouted(deviceId: 1, channel: ch));

        var notices = logger.Warnings(contains: "出现无法路由的通道");
        notices.Should().HaveCount(1, "每设备首次出现未路由通道只记一条提示，不应逐通道刷屏");
        notices[0].Should().Contain("device=1");
    }

    [Fact]
    public async Task UnroutedChannels_AreAggregatedPerDevice()
    {
        var logger = new CapturingLogger<PostgresMeasurementSink>();
        await using var ds = NewUnusedDataSource();
        var sink = new PostgresMeasurementSink(ds, new UnroutedResolver(), logger);

        // 两个设备交替上报未路由通道：各自应得到一条独立的首次提示
        for (ushort ch = 1; ch <= 5; ch++)
        {
            await sink.WriteAsync(Unrouted(deviceId: 1, channel: ch));
            await sink.WriteAsync(Unrouted(deviceId: 2, channel: ch));
        }

        var notices = logger.Warnings(contains: "出现无法路由的通道");
        notices.Should().HaveCount(2, "聚合按设备维度，两个设备各记一条，不是按通道刷屏");
        notices.Should().Contain(m => m.Contains("device=1"));
        notices.Should().Contain(m => m.Contains("device=2"));
    }

    // ---- helpers ----

    private static NpgsqlDataSource NewUnusedDataSource()
        // 仅构造，不连接；未路由分支不会执行任何 SQL。
        => NpgsqlDataSource.Create("Host=localhost;Database=unused;Username=unused;Password=unused");

    private static FxDecodedEnvelope Unrouted(long deviceId, ushort channel)
    {
        var cr = new FxChannelResultDto(
            Address: 1, Channel: channel, StatusCode: 0,
            EchoTimeNs: null, EchoTime2Ns: null, SpTempC: null, DeltaTofNs: null,
            Gain: 0f, EchoWave: null, LowAi: null, HighAi: null, Angle: null, MidResults: null);
        var ts = DateTimeOffset.UtcNow;
        return new FxDecodedEnvelope(deviceId, ts, ts, FxResultType.ChannelResult, ChannelResult: cr);
    }

    /// <summary>所有 (device,channel) 都解析为未路由（ChannelId=0,BoltId=0 → sink 的 default 分支）。</summary>
    private sealed class UnroutedResolver : IChannelResolver
    {
        public Task<ChannelRoute> ResolveAsync(long deviceId, ushort channelNo, DateTimeOffset firstSeenAt, CancellationToken ct = default)
            => Task.FromResult(new ChannelRoute(ChannelId: 0, BoltId: 0));
        public void Invalidate(long deviceId, ushort channelNo) { }
        public void InvalidateDevice(long deviceId) { }
        public void InvalidateAll() { }
    }
}
