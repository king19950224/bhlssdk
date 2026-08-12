using FluentAssertions;
using FxSDK.Models;
using FxSDK.Utility;
using Xunit;

namespace BoltPreload.Tests.Fx;

/// <summary>
/// 重放台自身的回归。重放台是 1.6 同源比对的量具，量具不准后面全白比：
/// 帧字节进不去、CSV 读不出、或者摘要少一行，比对结论就是错的还看不出来。
/// 这里用合成帧钉住管道（字节进、摘要出），真机声时的比对等现场样本录回来再做。
/// </summary>
public sealed class RawFrameReplayTests
{
    /// <summary>
    /// 手搓一条 0xA2 读参数回应帧（ProbeFreq），线格式与设备一致：
    /// 帧头(4) + 长度(2) + id(2) + 指令(1) + 通道(1) + 起始地址(1) + 数量(1) + 数据(2) + CRC(2) + 帧尾(4)。
    /// 选参数帧当自测样本是因为它不依赖会话里的 ChannelConfig，管道通不通一眼可辨。
    /// </summary>
    private static byte[] BuildProbeFreqFrame(ushort address, byte channel, ushort freqKhz)
    {
        var frame = new List<byte> { 0xDE, 0xDE, 0xDE, 0xDE };
        var payload = new List<byte>();
        payload.AddRange(BitConverter.GetBytes(address));
        payload.Add(0xA2);
        payload.Add(channel);
        payload.Add(0x00);                                  // 起始地址 0x00 = ProbeFreq
        payload.Add(0x01);                                  // 寄存器数量
        payload.AddRange(BitConverter.GetBytes(freqKhz));

        frame.AddRange(BitConverter.GetBytes((ushort)(payload.Count + 2)));
        frame.AddRange(payload);
        frame.AddRange(ByteUtility.CRC16_CCITT(frame.ToArray()));
        frame.AddRange(new byte[] { 0x00, 0x00, 0x00, 0x00 });
        return frame.ToArray();
    }

    [Fact]
    public void ParseHex_AcceptsSdkHexStringFormat()
    {
        // SDK 的 BytesToHexStr 产出“两位大写 + 空格”，末尾还挂一个空格，原样能吃
        RawFrameReplay.ParseHex("DE DE 12 00 ").Should().Equal(0xDE, 0xDE, 0x12, 0x00);
    }

    [Fact]
    public void ParseHex_AcceptsCompactAndMixedWhitespace()
    {
        RawFrameReplay.ParseHex("dede1200").Should().Equal(0xDE, 0xDE, 0x12, 0x00);
        RawFrameReplay.ParseHex("DE\tDE\n12 00").Should().Equal(0xDE, 0xDE, 0x12, 0x00);
    }

    [Fact]
    public void ParseHex_OddLength_Throws()
    {
        // 奇数长度只可能是样本被截断，硬吞半个字节会让后面整批帧错位
        var act = () => RawFrameReplay.ParseHex("DED");
        act.Should().Throw<FormatException>().WithMessage("*截断*");
    }

    [Fact]
    public void Replay_SyntheticFrame_ProducesSummary()
    {
        var frames = new[] { BuildProbeFreqFrame(address: 1, channel: 9, freqKhz: 2500) };

        var summaries = RawFrameReplay.Replay(frames);

        var s = summaries.Should().ContainSingle().Subject;
        s.Kind.Should().Be(nameof(DeviceParameter));
        s.Channel.Should().Be(9);
    }

    [Fact]
    public void Replay_MultipleFrames_DigestsWholeBuffer()
    {
        // 缓冲里塞三帧只出一条摘要的话，同源比对会拿残缺基线下结论
        var frames = new[]
        {
            BuildProbeFreqFrame(1, 9, 2500),
            BuildProbeFreqFrame(1, 10, 4000),
            BuildProbeFreqFrame(1, 11, 5000),
        };

        var summaries = RawFrameReplay.Replay(frames);

        summaries.Should().HaveCount(3);
        summaries.Select(s => s.Channel).Should().Equal((ushort)9, (ushort)10, (ushort)11);
    }

    [Fact]
    public void Replay_RunsAreIsolated()
    {
        // 每次重放各自新建会话、跑完清上下文；上一轮注入的配置漏到下一轮，比对结果就不可复现了
        var frame = new[] { BuildProbeFreqFrame(1, 9, 2500) };

        var first = RawFrameReplay.Replay(frame, channels:
            [new ChannelConfig { Address = 1, Channel = 9, ProbeFreq = 2m }]);
        var second = RawFrameReplay.Replay(frame);

        first.Should().ContainSingle();
        second.Should().ContainSingle();
    }

    [Fact]
    public void LoadRawFramesFromCsv_ReadsHexColumn_SkippingRowsWithoutIt()
    {
        // 按 RawWriter 的真实形状造样本：14 列、设备与波形列带引号、hex 列在最后
        var frame = BuildProbeFreqFrame(1, 9, 2500);
        var hex = ByteUtility.BytesToHexStr(frame);
        var csv = string.Join(Environment.NewLine,
            "时间,设备,地址,通道,状态,声时ns,声时差ns,原始温度,平滑温度,波形起点ns,点间隔ns,点数,波形,原始报文",
            $"2026-08-12 10:00:00.000,\"192.168.0.7:23\",1,9,0,32142.5,,,16.7,26000,125,3,\"120 -35 88\",{hex}",
            "2026-08-12 10:00:01.000,\"192.168.0.7:23\",1,10,0,,,,,0,0,0,\"\",");

        var path = Path.Combine(Path.GetTempPath(), $"replay-{Guid.NewGuid():N}.csv");
        try
        {
            File.WriteAllText(path, csv);

            var frames = RawFrameReplay.LoadRawFramesFromCsv(path);

            frames.Should().ContainSingle("第二行没录原始报文，只能跳过");
            frames[0].Should().Equal(frame);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void LoadRawFramesFromCsv_AllRowsEmpty_ThrowsWithActionableMessage()
    {
        // 整份 CSV 都没有帧字节 = 录制时没开 rawHex，这趟现场白跑了——必须当场喊出来
        var csv = string.Join(Environment.NewLine,
            "时间,设备,地址,通道,状态,声时ns,声时差ns,原始温度,平滑温度,波形起点ns,点间隔ns,点数,波形,原始报文",
            "2026-08-12 10:00:00.000,\"dev\",1,9,0,,,,,0,0,0,\"\",");

        var path = Path.Combine(Path.GetTempPath(), $"replay-{Guid.NewGuid():N}.csv");
        try
        {
            File.WriteAllText(path, csv);

            var act = () => RawFrameReplay.LoadRawFramesFromCsv(path);
            act.Should().Throw<InvalidOperationException>().WithMessage("*rawHex*");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ToBaselineCsv_IsStableAndDiffFriendly()
    {
        var summaries = new[]
        {
            new RawFrameReplay.ReplayFrameSummary("ChannelResult", 1, 9, 0, 32142.5m, 16.71m, 12.3m, 256),
            new RawFrameReplay.ReplayFrameSummary("DeviceParameter", 1, null, null, null, null, null, 0),
        };

        var csv = RawFrameReplay.ToBaselineCsv(summaries);

        var lines = csv.TrimEnd().Split(Environment.NewLine);
        lines.Should().HaveCount(3);
        lines[1].Should().Be("0,ChannelResult,1,9,0,32142.5,16.71,12.3,256");
        lines[2].Should().Be("1,DeviceParameter,1,,,,,,0");
    }
}
