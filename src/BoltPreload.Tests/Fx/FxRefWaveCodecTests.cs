using BoltPreload.Fx;
using FluentAssertions;
using FxSDK.Models;
using Xunit;

namespace BoltPreload.Tests.Fx;

/// <summary>
/// 钉住参考波形持久化格式：channels.ref_echo_wave 里躺着的是置零基线，
/// 编解码任何一端悄悄变了，自动法通道重启回灌就会拿错波形——宁可测试先红。
/// </summary>
public sealed class FxRefWaveCodecTests
{
    [Fact]
    public void PackUnpack_RoundTrip()
    {
        var wave = new EchoWave
        {
            EchoStart = 31000,
            ClockInt = 125,
            WaveData = new List<short> { 0, -120, 512, short.MaxValue, short.MinValue, 7 }
        };

        var restored = FxRefWaveCodec.TryUnpack(FxRefWaveCodec.Pack(wave));

        restored.Should().NotBeNull();
        restored!.EchoStart.Should().Be(31000u);
        restored.ClockInt.Should().Be((ushort)125);
        restored.WaveData.Should().Equal(0, -120, 512, short.MaxValue, short.MinValue, 7);
    }

    [Fact]
    public void PackUnpack_EmptyWaveData_RoundTrip()
    {
        var wave = new EchoWave { EchoStart = 5, ClockInt = 10, WaveData = null };

        var restored = FxRefWaveCodec.TryUnpack(FxRefWaveCodec.Pack(wave));

        restored.Should().NotBeNull();
        restored!.WaveData.Should().BeEmpty();
    }

    [Theory]
    [InlineData(null)]                       // 库里没存过
    [InlineData(new byte[0])]                // 空串
    [InlineData(new byte[] { 9, 1, 2, 3 })]  // 版本号不认识
    public void TryUnpack_BadInput_ReturnsNull(byte[]? packed)
    {
        FxRefWaveCodec.TryUnpack(packed).Should().BeNull();
    }

    [Fact]
    public void TryUnpack_TruncatedPayload_ReturnsNull()
    {
        // 声称 6 个点但字节被截断：半截波形当基线比没基线更危险，必须整体拒收
        var packed = FxRefWaveCodec.Pack(new EchoWave
        {
            EchoStart = 100,
            ClockInt = 125,
            WaveData = new List<short> { 1, 2, 3, 4, 5, 6 }
        });

        FxRefWaveCodec.TryUnpack(packed[..^2]).Should().BeNull();
    }
}
