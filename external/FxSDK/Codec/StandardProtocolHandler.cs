using FxSDK.Core;
using FxSDK.Models;

namespace FxSDK.Codec;

/// <summary>
/// 标准版设备编解码处理器
/// </summary>
public class StandardProtocolHandler : GeneralProtocolHandler
{
    public StandardProtocolHandler()
    {
        DelayMilliseconds = 5;
    }

    public override IReadOnlyList<object> EncodeWorkMode(IoSession session, WorkMode mode, ushort channel = 0, ushort address = 1)
    {
        if (mode == WorkMode.MultiMeas)//标准版设备 多通道监测模式等同于单通道测量模式
        {
            var encodeMessages = new List<object>();
            byte commandCode = 0x03;
            var commandParams = new byte[1];
            commandParams[0] = 0x00;
            encodeMessages.Add(EncodeWriteParamCmd(commandCode, commandParams));
            return encodeMessages;
        }
        return base.EncodeWorkMode(session, mode, channel, address);
    }
    /// <summary>
    /// 标准版的多通道监测模式 结果实际上是单通道结果模式
    /// </summary>
    protected override List<DecodedResult> DoDecode(IoSession session, CancellationToken token)
    {
        var decodedResults = new List<DecodedResult>();
        var deviceMode = SessionContext.GetDeviceMode(session);
        if (deviceMode == WorkMode.MultiMeas)
        {
            decodedResults.AddRange(DecodeChannelResults(session,token));
        }
        else
        {
            decodedResults.AddRange(base.DoDecode(session, token));
        }
        return decodedResults;
    }
}