using BoltPreload.Application.Ingestion;
using BoltPreload.Fx.Models;
using BoltPreload.Infrastructure.Ingestion;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace BoltPreload.Tests.Ingestion;

public class LocalFileMeasurementWalTests : IDisposable
{
    private readonly string _tempRoot;

    public LocalFileMeasurementWalTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "waltest_" + Guid.NewGuid().ToString("N"));
    }

    private LocalFileMeasurementWal NewWal(int maxEntries = 50_000)
    {
        var opt = Options.Create(new MeasurementWalOptions { RootPath = _tempRoot, MaxEntries = maxEntries });
        return new LocalFileMeasurementWal(opt, NullLogger<LocalFileMeasurementWal>.Instance);
    }

    private static FxDecodedEnvelope Measurement(long deviceId, ushort channel, DateTimeOffset ts, short[]? wave = null)
    {
        FxEchoWaveDto? echo = wave is null ? null : new FxEchoWaveDto(5, 23000, 25, wave, -490);
        var cr = new FxChannelResultDto(
            Address: 1, Channel: channel, StatusCode: 0,
            EchoTimeNs: 32000m, EchoTime2Ns: null, SpTempC: 16.7m, DeltaTofNs: 12.3m,
            Gain: 16f, EchoWave: echo, LowAi: null, HighAi: null, Angle: null, MidResults: null);
        return new FxDecodedEnvelope(deviceId, ts, ts, FxResultType.ChannelResult, ChannelResult: cr);
    }

    [Fact]
    public async Task Append_then_Replay_drains_in_fifo_order()
    {
        var wal = NewWal();
        var ts = DateTimeOffset.UtcNow;
        await wal.AppendAsync(Measurement(1, 9, ts));
        await wal.AppendAsync(Measurement(2, 9, ts));
        await wal.AppendAsync(Measurement(3, 9, ts));
        wal.PendingCount.Should().Be(3);

        var seen = new List<long>();
        var replayed = await wal.ReplayAsync((env, _) => { seen.Add(env.DeviceId); return Task.CompletedTask; });

        replayed.Should().Be(3);
        seen.Should().Equal(1L, 2L, 3L);
        wal.PendingCount.Should().Be(0);
    }

    [Fact]
    public async Task Replay_stops_on_write_failure_and_keeps_remaining()
    {
        var wal = NewWal();
        var ts = DateTimeOffset.UtcNow;
        await wal.AppendAsync(Measurement(1, 9, ts));
        await wal.AppendAsync(Measurement(2, 9, ts));
        await wal.AppendAsync(Measurement(3, 9, ts));

        var attempt = 0;
        var replayed = await wal.ReplayAsync((_, _) =>
        {
            attempt++;
            return attempt == 2
                ? throw new InvalidOperationException("db down")
                : Task.CompletedTask;
        });

        replayed.Should().Be(1);          // 第 1 条成功，第 2 条失败即停
        wal.PendingCount.Should().Be(2);  // 第 2、3 条保留待下轮

        // DB 恢复后再重放，按序补完剩余 2 条
        var seen = new List<long>();
        var replayed2 = await wal.ReplayAsync((env, _) => { seen.Add(env.DeviceId); return Task.CompletedTask; });
        replayed2.Should().Be(2);
        seen.Should().Equal(2L, 3L);
        wal.PendingCount.Should().Be(0);
    }

    [Fact]
    public async Task Append_ignores_non_measurement_envelope()
    {
        var wal = NewWal();
        var ts = DateTimeOffset.UtcNow;
        var dp = new FxDecodedEnvelope(1, ts, ts, FxResultType.DeviceParameter,
            DeviceParameter: new FxDeviceParameterDto(1, 9, "PROP_GAIN", 16));

        await wal.AppendAsync(dp);

        wal.PendingCount.Should().Be(0);
    }

    [Fact]
    public async Task Append_over_capacity_drops_oldest()
    {
        var wal = NewWal(maxEntries: 3);
        var ts = DateTimeOffset.UtcNow;
        for (long i = 1; i <= 5; i++)
            await wal.AppendAsync(Measurement(i, 9, ts));

        wal.PendingCount.Should().Be(3);

        var seen = new List<long>();
        await wal.ReplayAsync((env, _) => { seen.Add(env.DeviceId); return Task.CompletedTask; });
        seen.Should().Equal(3L, 4L, 5L); // 最旧的 1、2 被丢弃
    }

    [Fact]
    public async Task Replay_quarantines_corrupt_entry_and_continues()
    {
        Directory.CreateDirectory(_tempRoot);
        // 手动写一个损坏条目，命名靠前（排在队头），验证它不会堵塞后续
        var corruptPath = Path.Combine(_tempRoot, "0000000000001_000000001.json");
        await File.WriteAllTextAsync(corruptPath, "{ this is not valid json");

        var wal = NewWal();
        await wal.AppendAsync(Measurement(7, 9, DateTimeOffset.UtcNow));

        var seen = new List<long>();
        var replayed = await wal.ReplayAsync((env, _) => { seen.Add(env.DeviceId); return Task.CompletedTask; });

        replayed.Should().Be(1);
        seen.Should().Equal(7L);
        wal.PendingCount.Should().Be(0);
        File.Exists(corruptPath + ".corrupt").Should().BeTrue();
    }

    [Fact]
    public async Task Replay_round_trips_multichannel_and_strips_wave()
    {
        var wal = NewWal();
        var ts = new DateTimeOffset(2026, 5, 21, 8, 30, 0, TimeSpan.Zero);
        var wave = new short[] { -5, 0, 7, 123, -120 };
        var ch9 = new FxChannelResultDto(1, 9, 0, 32000m, null, 16.7m, 11.1m, 16f,
            new FxEchoWaveDto(5, 23000, 25, wave, -490), null, null, null, new decimal[] { 1.5m, 2.5m });
        var ch10 = new FxChannelResultDto(1, 10, -1, 31000m, null, 16.9m, null, 8f,
            null, null, null, null, null);
        var multi = new FxMultiChannelResultDto(new Dictionary<ushort, FxChannelResultDto> { [9] = ch9, [10] = ch10 });
        var env = new FxDecodedEnvelope(42, ts, ts, FxResultType.MultiChannelResult, MultiChannelResult: multi);

        await wal.AppendAsync(env);

        FxDecodedEnvelope? got = null;
        await wal.ReplayAsync((e, _) => { got = e; return Task.CompletedTask; });

        got.Should().NotBeNull();
        got!.DeviceId.Should().Be(42);
        got.ResultType.Should().Be(FxResultType.MultiChannelResult);
        got.MultiChannelResult!.Channels.Should().ContainKeys((ushort)9, (ushort)10);

        var g9 = got.MultiChannelResult.Channels[9];
        g9.EchoTimeNs.Should().Be(32000m);
        g9.SpTempC.Should().Be(16.7m);
        g9.MidResults.Should().Equal(1.5m, 2.5m);
        // WAL 只兜底测量元数据：波形在落盘前被剥离（避免 DB 故障窗口把磁盘/CPU 打爆），不随重放恢复
        g9.EchoWave.Should().BeNull();
        got.MultiChannelResult.Channels[10].StatusCode.Should().Be((short)-1);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempRoot))
            Directory.Delete(_tempRoot, recursive: true);
        GC.SuppressFinalize(this);
    }
}
