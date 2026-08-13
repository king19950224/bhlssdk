using System.Globalization;
using System.Text;
using FxSDK.Codec;
using FxSDK.Core;
using FxSDK.Models;
using FxSDK.Utility;

namespace BoltPreload.Tests.Fx;

/// <summary>
/// 离线重放台：把录下来的原始帧字节喂给 SDK 解码器，收 <see cref="DecodedResult"/>，出与 SDK 版本无关的摘要。
/// <para>
/// 这是 1.6 升级同源比对的地基（升级评估 §9 阶段 A 第 2 步）。比对的唯一可信做法是拿同一批
/// 真机字节分别喂 1.4 和 1.6，逐帧比 EchoTime——进程内模拟器绕过 SDK 直接产 DTO，
/// tools/FxDeviceSimulator 还是空壳，两个都干不了这活，所以单独搭这一个。
/// </para>
/// <para>
/// 用法：现场用 bhls1.6/FxReader（rawDir 配上、rawHex=true）录 raw-*.csv，
/// 拷回来 <see cref="LoadRawFramesFromCsv"/> 读出字节，<see cref="Replay"/> 得到 1.4 基线，
/// <see cref="ToBaselineCsv"/> 落盘存档。1.6 适配完后同样字节再跑一遍，两份 CSV 一 diff 就见分晓。
/// </para>
/// <para>
/// 别忘了注入配置：SDK 只有在会话里有对应 (address, channel) 的 ChannelConfig 且 ProbeFreq&gt;0、
/// 且工作模式是 MultiMeas 时才算 EchoTime。少配一样，声时全 null 而状态码还是 0——
/// 和真机接入时那个坑是同一个（sdk-api-facts §1.1）。
/// </para>
/// </summary>
public static class RawFrameReplay
{
    /// <summary>一帧的解码摘要。字段刻意只留两版都有、且比对有意义的：不含波形本体（太大），不含 SDK 专属类型。</summary>
    /// <param name="Kind">ChannelResult / MultiChannelResult / DeviceParameter。</param>
    /// <param name="Address">设备地址。</param>
    /// <param name="Channel">通道号（DeviceParameter 无通道时为 null）。</param>
    /// <param name="StatusCode">状态码。1.6 侧是枚举，转短整型后同一口径。</param>
    /// <param name="EchoTimeNs">声时（ns），同源比对的主角。</param>
    /// <param name="SpTempC">温度。1.6 语义变了（平滑值），比对时本列对不上属预期，见评估 §3.1。</param>
    /// <param name="DeltaTofNs">SDK 自算的声时差，留作旁证。</param>
    /// <param name="WavePoints">波形点数，用来确认两版吃到的是同一帧。</param>
    public sealed record ReplayFrameSummary(
        string Kind,
        ushort Address,
        ushort? Channel,
        short? StatusCode,
        decimal? EchoTimeNs,
        decimal? SpTempC,
        decimal? DeltaTofNs,
        int WavePoints);

    /// <summary>喂重放的最小会话：解码链只拿 SessionId 当上下文键，不碰网络。</summary>
    private sealed class ReplaySession : IoSession
    {
        public ReplaySession(FxProtocolHandler handler) : base(handler)
        {
            SessionId = "replay:" + Guid.NewGuid().ToString("N")[..8];
            SessionToken = CancellationToken.None;
        }

        protected override CancellationToken SessionToken { get; init; }

        protected override Task SendDataAsync(byte[] data) => Task.CompletedTask;

        // 1.6 新增的抽象成员。重放没有连接可关，空实现
        protected override void RequestClose(SessionCloseReason reason, Exception? exception) { }
    }

    /// <summary>
    /// 把一批原始帧喂给 model=3 解码器，返回逐帧摘要。
    /// 每次调用新建 handler 与会话，跑完清上下文——重放之间互不串味。
    /// </summary>
    /// <param name="rawFrames">帧字节，按录制顺序。</param>
    /// <param name="device">设备配置，null 用 model=3 直连的默认值。</param>
    /// <param name="channels">要注入会话的通道配置；重放测量帧又想让 SDK 算声时，这个必须给。</param>
    /// <param name="workMode">注入的工作模式。测量帧按监测主模式 MultiMeas 解，参数回应帧无所谓。</param>
    public static IReadOnlyList<ReplayFrameSummary> Replay(
        IEnumerable<byte[]> rawFrames,
        DeviceConfig? device = null,
        IEnumerable<ChannelConfig>? channels = null,
        WorkMode workMode = WorkMode.MultiMeas)
    {
        // 温度口径对齐生产：FxConnectorFactory 静态构造把全局平滑设为 Direct（关平滑保历史口径），
        // 重放台不经过工厂，不设的话 SpTemp 会吃 SDK 默认 τ=180s 平滑——同一帧重放出不同温度，
        // 同源比对的温度列就废了（声时列不受影响，平滑不进 EchoTime 计算）。
        FxUtility.TempTauProfile = TempTauProfile.Direct;

        var handler = new MultiChIotProtocolHandler();
        var session = new ReplaySession(handler);
        try
        {
            var dev = device ?? new DeviceConfig { Model = 3, CommunicationType = 1, TransferMode = 1, Address = 1 };
            SessionContext.StoreToSession(session, dev);
            SessionContext.StoreToSession(session, workMode, dev.Address);
            if (channels is not null)
            {
                foreach (var ch in channels)
                    SessionContext.StoreToSession(session, ch);
            }

            foreach (var frame in rawFrames)
                handler.Enqueue(frame);

            var results = new List<DecodedResult>();
            // TryDecode 一次消化整个缓冲、缓冲空了立刻返回空表；多转几圈是防实现改成一次一帧
            using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (!guard.IsCancellationRequested)
            {
                var batch = handler.TryDecode(session, guard.Token);
                if (batch.Count == 0) break;
                results.AddRange(batch);
            }

            return results.SelectMany(Summarize).ToArray();
        }
        finally
        {
            SessionContext.ClearContext(session);
        }
    }

    /// <summary>把一个解码结果拍平成摘要。多通道结果按通道展开成多行，diff 时一行对一行。</summary>
    private static IEnumerable<ReplayFrameSummary> Summarize(DecodedResult result)
    {
        switch (result)
        {
            case ChannelResult cr:
                yield return new ReplayFrameSummary(
                    nameof(ChannelResult), cr.Address, cr.Channel, cr.StatusCode.ToCode(),
                    cr.EchoTime, cr.SpTemp, cr.DeltaTof, cr.EchoWave?.WaveData?.Count ?? 0);
                break;

            case MultiChannelResult mc:
                foreach (var (channelNo, cr) in mc.Results.OrderBy(kv => kv.Key))
                    yield return new ReplayFrameSummary(
                        nameof(MultiChannelResult), cr.Address, channelNo, cr.StatusCode.ToCode(),
                        cr.EchoTime, cr.SpTemp, cr.DeltaTof, cr.EchoWave?.WaveData?.Count ?? 0);
                break;

            case DeviceParameter dp:
                yield return new ReplayFrameSummary(
                    nameof(DeviceParameter), dp.Address, dp.Channel, null, null, null, null, 0);
                break;

            default:
                yield return new ReplayFrameSummary(
                    result.GetType().Name, result.Address, null, null, null, null, null, 0);
                break;
        }
    }

    /// <summary>
    /// 十六进制转字节。容两种写法：SDK 的 <c>BytesToHexStr</c> 是两位大写加空格（“DE DE 12 ”），
    /// 手工样本常是连写（“DEDE12”）。别的分隔符（换行、制表）一并容掉。
    /// </summary>
    public static byte[] ParseHex(string hex)
    {
        var compact = new StringBuilder(hex.Length);
        foreach (var c in hex)
        {
            if (!char.IsWhiteSpace(c))
                compact.Append(c);
        }
        if (compact.Length % 2 != 0)
            throw new FormatException($"十六进制长度是奇数（{compact.Length}），样本被截断过");

        var bytes = new byte[compact.Length / 2];
        for (var i = 0; i < bytes.Length; i++)
            bytes[i] = byte.Parse(compact.ToString(i * 2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        return bytes;
    }

    /// <summary>
    /// 读 FxReader 的 raw-*.csv，取“原始报文”列还原成帧字节。
    /// 该列只有 FxReader 开了 rawHex（内部走 DeveloperMode）才有内容，空的行跳过——
    /// 拿到一份全空的 CSV 说明录制时没开开关，白跑一趟现场，所以一帧都没有时直接报错提醒。
    /// </summary>
    public static IReadOnlyList<byte[]> LoadRawFramesFromCsv(string csvPath)
    {
        var frames = new List<byte[]>();
        var lines = File.ReadAllLines(csvPath, Encoding.UTF8);
        foreach (var line in lines.Skip(1))    // 首行表头
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var fields = SplitCsvLine(line);
            // RawWriter 固定 14 列，最后一列是原始报文
            if (fields.Count < 14) continue;
            var hex = fields[13].Trim();
            if (hex.Length == 0) continue;
            frames.Add(ParseHex(hex));
        }

        if (frames.Count == 0)
            throw new InvalidOperationException(
                $"{Path.GetFileName(csvPath)} 里一条原始报文都没有。录制时要开 FxReader 的 rawHex=true，" +
                "否则 CSV 只有解码结果、没有帧字节，做不了同源比对。");
        return frames;
    }

    /// <summary>摘要落成 CSV 基线。1.4 与 1.6 各出一份，diff 这两个文件就是同源比对的结论。</summary>
    public static string ToBaselineCsv(IEnumerable<ReplayFrameSummary> summaries)
    {
        var sb = new StringBuilder();
        sb.AppendLine("序号,类型,地址,通道,状态码,声时ns,温度,声时差ns,波形点数");
        var i = 0;
        foreach (var s in summaries)
        {
            sb.Append(CultureInfo.InvariantCulture, $"{i++},{s.Kind},{s.Address},{s.Channel},{s.StatusCode},");
            sb.Append(CultureInfo.InvariantCulture, $"{s.EchoTimeNs},{s.SpTempC},{s.DeltaTofNs},{s.WavePoints}");
            sb.AppendLine();
        }
        return sb.ToString();
    }

    /// <summary>切一行 CSV。只处理 RawWriter 会产出的形状：逗号分隔、双引号包裹、内部引号写成两个。</summary>
    private static List<string> SplitCsvLine(string line)
    {
        var fields = new List<string>();
        var current = new StringBuilder();
        var inQuotes = false;
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (inQuotes)
            {
                if (c == '"')
                {
                    if (i + 1 < line.Length && line[i + 1] == '"')
                    {
                        current.Append('"');
                        i++;
                    }
                    else
                    {
                        inQuotes = false;
                    }
                }
                else
                {
                    current.Append(c);
                }
            }
            else if (c == '"')
            {
                inQuotes = true;
            }
            else if (c == ',')
            {
                fields.Add(current.ToString());
                current.Clear();
            }
            else
            {
                current.Append(c);
            }
        }
        fields.Add(current.ToString());
        return fields;
    }
}
