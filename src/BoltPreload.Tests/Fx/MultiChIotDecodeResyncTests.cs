using FluentAssertions;
using FxSDK.Codec;
using FxSDK.Core;
using FxSDK.Models;
using FxSDK.Utility;
using Xunit;

namespace BoltPreload.Tests.Fx;

/// <summary>
/// 锁定 MultiChIot 解码器的坏帧重同步行为：CRC 校验失败的帧要被跳过（缓冲前进），
/// 后续好帧照常解码。
/// <para>
/// 此前 CRC 失败分支什么都不做：缓冲不前进，解码在同一坏帧上空转到超时，
/// TCP 连接和心跳还在但不再出数据，只能重启进程恢复。链路上一个误码字节就会触发。
/// </para>
/// </summary>
public sealed class MultiChIotDecodeResyncTests
{
    /// <summary>最小可用会话：解码路径只用 SessionId 做 SessionContext 键，不碰网络。</summary>
    private sealed class FakeSession : IoSession
    {
        public FakeSession(FxProtocolHandler handler) : base(handler)
        {
            SessionId = "test:" + Guid.NewGuid().ToString("N")[..8];
            SessionToken = CancellationToken.None;
        }

        protected override CancellationToken SessionToken { get; init; }

        protected override Task SendDataAsync(byte[] data) => Task.CompletedTask;
    }

    /// <summary>
    /// 构造一条 0xA2（读参数回应）通道 ProbeFreq 帧，线格式与设备一致：
    /// 帧头(4) + 长度(2) + id(2) + 指令(1) + 通道(1) + 起始地址(1) + 数量(1) + 数据(2) + CRC(2) + 帧尾(4)。
    /// </summary>
    private static byte[] BuildProbeFreqResponse(ushort address, byte channel, ushort freqKhz, bool corruptCrc = false)
    {
        var frame = new List<byte> { 0xDE, 0xDE, 0xDE, 0xDE };
        var payload = new List<byte>();
        payload.AddRange(BitConverter.GetBytes(address));   // id
        payload.Add(0xA2);                                  // 读参数回应
        payload.Add(channel);
        payload.Add(0x00);                                  // 起始地址 0x00 = ProbeFreq
        payload.Add(0x01);                                  // 寄存器数量
        payload.AddRange(BitConverter.GetBytes(freqKhz));   // 数据（kHz）

        frame.AddRange(BitConverter.GetBytes((ushort)(payload.Count + 2))); // 长度 = 有效信息 + CRC
        frame.AddRange(payload);

        var crc = ByteUtility.CRC16_CCITT(frame.ToArray());
        if (corruptCrc)
            crc[0] ^= 0xFF; // 模拟链路误码：CRC 对不上

        frame.AddRange(crc);
        frame.AddRange(new byte[] { 0x00, 0x00, 0x00, 0x00 }); // 帧尾
        return frame.ToArray();
    }

    /// <summary>带 2s 守卫的解码调用：回归时（解码器空转）测试快速失败而不是吊死整个测试套件。</summary>
    private static List<DecodedResult> DecodeWithGuard(MultiChIotProtocolHandler handler, IoSession session)
    {
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        return handler.TryDecode(session, guard.Token);
    }

    [Fact]
    public void GoodFrame_DecodesProbeFreq()
    {
        var handler = new MultiChIotProtocolHandler();
        var session = new FakeSession(handler);

        handler.Enqueue(BuildProbeFreqResponse(address: 1, channel: 9, freqKhz: 2500));

        var results = DecodeWithGuard(handler, session);

        var param = results.OfType<DeviceParameter>().Should().ContainSingle().Subject;
        param.ParamName.Should().Be(nameof(ChannelConfig.ProbeFreq));
        param.ParamValue.Should().Be(2.5m, "2500 kHz → 2.5 MHz");
        param.Channel.Should().Be(9);
    }

    [Fact]
    public void CorruptedCrcFrame_IsSkipped_FollowingGoodFrameStillDecodes()
    {
        var handler = new MultiChIotProtocolHandler();
        var session = new FakeSession(handler);

        // 坏帧在前（CRC 被打坏），好帧紧随其后。旧实现会卡在坏帧上，好帧解不出来
        handler.Enqueue(BuildProbeFreqResponse(address: 1, channel: 9, freqKhz: 2500, corruptCrc: true));
        handler.Enqueue(BuildProbeFreqResponse(address: 1, channel: 10, freqKhz: 4000));

        var results = DecodeWithGuard(handler, session);

        var param = results.OfType<DeviceParameter>().Should()
            .ContainSingle("坏帧应被跳过，后续好帧照常解码").Subject;
        param.Channel.Should().Be(10, "解出来的应是坏帧后面的好帧");
        param.ParamValue.Should().Be(4m);
    }

    [Fact]
    public void ConsecutiveCorruptedFrames_DoNotStallDecoder()
    {
        var handler = new MultiChIotProtocolHandler();
        var session = new FakeSession(handler);

        // 连续多条坏帧 + 一条好帧：重同步必须能连续跨过多条坏帧
        for (byte ch = 1; ch <= 5; ch++)
            handler.Enqueue(BuildProbeFreqResponse(address: 1, channel: ch, freqKhz: 2500, corruptCrc: true));
        handler.Enqueue(BuildProbeFreqResponse(address: 1, channel: 20, freqKhz: 2500));

        var results = DecodeWithGuard(handler, session);

        results.OfType<DeviceParameter>().Should()
            .ContainSingle("五条坏帧全部跳过，只有好帧产出结果")
            .Which.Channel.Should().Be(20);
    }
}
