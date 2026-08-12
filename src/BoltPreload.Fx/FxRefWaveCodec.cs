using System.Buffers.Binary;
using FxSDK.Models;

namespace BoltPreload.Fx;

/// <summary>
/// 参考波形（置零基线里的 LEchoWave）持久化编解码：<c>channels.ref_echo_wave</c>(bytea) ↔ SDK <c>EchoWave</c>。
/// <para>
/// SDK 1.6 的自动法是有状态的：置零锚点（RefEnvTime/RefPeriodNs）要配着参考波形回灌进 SessionContext
/// 才起作用，缺波形等于每次重启都重新捕获、有跨载波周期的风险。字段集对齐厂家参考实现的 ZeroRecord
/// （EchoStart/ClockInt/WaveData 三样），Fc/Thr 基线回灌用不上，真需要时升版本号扩展。
/// </para>
/// </summary>
public static class FxRefWaveCodec
{
    // 布局：[ver:1][EchoStart:u32][ClockInt:u16][count:i32][count × i16]，全小端
    private const byte Version = 1;
    private const int HeaderBytes = 1 + 4 + 2 + 4;

    public static byte[] Pack(EchoWave wave)
    {
        var data = wave.WaveData;
        var count = data?.Count ?? 0;
        var buf = new byte[HeaderBytes + count * 2];
        buf[0] = Version;
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(1), wave.EchoStart);
        BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(5), wave.ClockInt);
        BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(7), count);
        for (var i = 0; i < count; i++)
            BinaryPrimitives.WriteInt16LittleEndian(buf.AsSpan(HeaderBytes + i * 2), data![i]);
        return buf;
    }

    /// <summary>
    /// 坏数据一律返回 null：宁可当"没有参考波形"走 SDK 的首帧捕获，也不能拿半截波形当基线。
    /// </summary>
    public static EchoWave? TryUnpack(byte[]? packed)
    {
        if (packed is null || packed.Length < HeaderBytes || packed[0] != Version)
            return null;

        var count = BinaryPrimitives.ReadInt32LittleEndian(packed.AsSpan(7));
        if (count < 0 || packed.Length != HeaderBytes + count * 2)
            return null;

        var data = new List<short>(count);
        for (var i = 0; i < count; i++)
            data.Add(BinaryPrimitives.ReadInt16LittleEndian(packed.AsSpan(HeaderBytes + i * 2)));

        return new EchoWave
        {
            EchoStart = BinaryPrimitives.ReadUInt32LittleEndian(packed.AsSpan(1)),
            ClockInt = BinaryPrimitives.ReadUInt16LittleEndian(packed.AsSpan(5)),
            WaveData = data
        };
    }
}
