using FxSDK.Core;
using FxSDK.Models;
using FxSDK.Utility;

namespace FxSDK.Codec;

/// <summary>
/// 28通道高速版协议处理器。
/// 配置、波形和单通道结果复用标准/专家兼容逻辑，仅多通道高速结果帧独立解析。
/// </summary>
public sealed class UltraHighRate28ProtocolHandler : GeneralProtocolHandler
{
    private static readonly byte[] DecHighRate28FrameHead = {0xDF, 0xDF, 0xDF, 0xDF};
    private static readonly byte[] DecHighRate28FrameTail = {0xA5, 0xA5, 0xA5, 0xA5};
    private const int HighRate28ChannelCount = 28;
    private const int HighRate28FrameSize = 130;
    private const int TemperatureOffset = 6;
    private const int TemperatureReferenceOffset = 10;
    private const int ChannelTofOffset = 14;
    protected override int MaxContextBytes => 2 * 1024 * 1024;

    public UltraHighRate28ProtocolHandler()
    {
        DelayMilliseconds = 5;
    }

    protected override List<MultiChannelResult> DecodeMultiChannelResults(IoSession session, CancellationToken token)
    {
        var decodedList = new List<MultiChannelResult>();
        try
        {
            var device = SessionContext.GetDeviceConfig(session);
            if (device == null)
            {
                return decodedList;
            }

            var frameCount = 0;
            while (!token.IsCancellationRequested && ContextData.Count >= HighRate28FrameSize)
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    if (HeadBeginIdx < 0)
                    {
                        HeadBeginIdx = ByteUtility.IndexOf(ContextData, DecHighRate28FrameHead);
                        if (HeadBeginIdx < 0)
                        {
                            var removeRange = ContextData.Count - DecHighRate28FrameHead.Length;
                            ConsumeContext(removeRange);
                            break;
                        }
                    }

                    if (HeadBeginIdx > 0)
                    {
                        ConsumeContext(HeadBeginIdx);
                        HeadBeginIdx = 0;
                    }

                    if (ContextData.Count < HighRate28FrameSize)
                    {
                        break;
                    }

                    var decodedFrame = ContextData.GetRange(0, HighRate28FrameSize);
                    if (!IsFrameTailValid(decodedFrame, DecHighRate28FrameTail))
                    {
                        ContextData.RemoveAt(0);
                        HeadBeginIdx = -1;
                        break;
                    }

                    lock (AcqTimeLock)
                    {
                        CurrentAcqTime = CurrentAcqTime?.AddSeconds((double) device.SampleInterval) ?? DateTime.Now;
                        if (CurrentAcqTime - SetWorkModeTime < TimeSpan.FromMilliseconds(DiscardMs))
                        {
                            ConsumeContext(decodedFrame.Count);
                            HeadBeginIdx = -1;
                            continue;
                        }
                    }

                    var multiChResult = DecodeHighRate28Frame(session, device, decodedFrame, CurrentAcqTime.Value);
                    decodedList.Add(multiChResult);
                    ConsumeContext(decodedFrame.Count);
                    HeadBeginIdx = -1;

                    frameCount++;
                    if (frameCount > MaxFramesPerBatch)
                    {
                        break;
                    }
                }
                catch (Exception ex)
                {
                    LogDecodeWarning(session, nameof(DecodeMultiChannelResults), HeadBeginIdx, ex);
                    ContextData.RemoveAt(0);
                    HeadBeginIdx = -1;
                    break;
                }
            }
        }
        catch (Exception ex)
        {
            LogDecodeWarning(session, $"{nameof(DecodeMultiChannelResults)}Outer", HeadBeginIdx, ex);
            if (ContextData.Count > 0)
            {
                ContextData.RemoveAt(0);
            }
            HeadBeginIdx = -1;
        }

        return decodedList;
    }

    private MultiChannelResult DecodeHighRate28Frame(IoSession session, DeviceConfig device, List<byte> decodedFrame,
        DateTime receiveTime)
    {
        var enabledChannels = device.MCRConfig?.EnabledChannels ?? new List<ushort>();
        var channelCount = Math.Min(HighRate28ChannelCount, (int) device.UsChannelCount);
        var multiChResult = new MultiChannelResult
        {
            HexString = BuildDiagnosticHexString(decodedFrame),
            ReceiveTime = receiveTime,
            Results = new Dictionary<ushort, ChannelResult>(),
        };

        var frameTemperatures = new Dictionary<ushort, decimal>();
        var frameTemperature = DecodeFrameTemperature(decodedFrame);
        if (frameTemperature.HasValue)
        {
            for (var i = 0; i < channelCount; i++)
            {
                var channel = Convert.ToUInt16(i + 1);
                frameTemperatures[channel] = frameTemperature.Value;
            }
        }

        for (var i = 0; i < channelCount; i++)
        {
            var channel = Convert.ToUInt16(i + 1);
            if (enabledChannels.Count > 0 && !enabledChannels.Contains(channel))
            {
                continue;
            }

            var echoTime = DecodeExpertMultiChannelTof(decodedFrame, ChannelTofOffset + i * 4);
            if (!echoTime.HasValue)
            {
                continue;
            }

            multiChResult.Results[channel] = new ChannelResult
            {
                Channel = channel,
                EchoTime = echoTime.Value,
                ReceiveTime = receiveTime,
            };
        }

        var availableChannelTemps = frameTemperatures.Count > 0
            ? frameTemperatures
                .Select(item => (Address: device.Address, Channel: item.Key, Temperature: item.Value))
                .ToList()
            : SessionContext.GetChannelTemps(session);
        foreach (var kvResult in multiChResult.Results)
        {
            var chResult = kvResult.Value;
            chResult.RawTemp = ResolveSourceChannelTemperature(session, device.Address, kvResult.Key,
                frameTemperatures: frameTemperatures);
        }

        foreach (var kvResult in multiChResult.Results)
        {
            var chConfig = SessionContext.GetChannelConfig(session, device.Address, kvResult.Key);
            var chResult = kvResult.Value;
            chResult.EchoTime = (decimal) GetMedianValue(session, chResult.Address, chResult.Channel,
                nameof(ChannelResult.EchoTime), (double) chResult.EchoTime!);
            ResolveEffectiveChannelTemperature(session, chResult, chConfig, availableChannelTemps,
                chResult.ReceiveTime);
            chResult.DeltaTof = CalculateDeltaTof(chConfig, (decimal) chResult.EchoTime!,
                chResult.SpTemp);
        }

        return multiChResult;
    }

    private decimal? DecodeFrameTemperature(List<byte> decodedFrame)
    {
        try
        {
            if (!ByteUtility.TryReadUInt32BigEndian(decodedFrame, TemperatureOffset, out var ptRes)
                || !ByteUtility.TryReadUInt32BigEndian(decodedFrame, TemperatureReferenceOffset, out var refRes))
            {
                return null;
            }

            return Pt1000Decoder.GetResult(ptRes, refRes);
        }
        catch
        {
            return null;
        }
    }

    private static decimal? DecodeExpertMultiChannelTof(List<byte> decodedFrame, int offset)
    {
        if (!ByteUtility.TryReadUInt32BigEndian(decodedFrame, offset, out var tofAll))
        {
            return null;
        }

        var tofIntData = (tofAll >> 12) * OriginalInterval;
        var tofDecData = (tofAll & 0xFFF) * OriginalInterval / 4096M;
        return tofIntData + tofDecData;
    }
}
