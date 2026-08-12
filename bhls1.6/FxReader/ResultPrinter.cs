using System.Text;
using System.Threading.Channels;
using FxSDK.Models;

namespace FxReader;

readonly record struct Sample(
    string Device,
    DateTime Time,
    ushort Address,
    ushort Channel,
    decimal? EchoTime,
    decimal? DeltaTof,
    decimal? Temp,
    MeasStatusCode Status,
    int WavePoints,
    string? Hex);

// 事件线程只入队，打印交给后台
sealed class ResultPrinter
{
    private readonly Channel<Sample> _queue = Channel.CreateBounded<Sample>(
        new BoundedChannelOptions(4096)
        {
            SingleReader = true,
            FullMode = BoundedChannelFullMode.Wait
        });

    private readonly Dictionary<(string Device, ushort Address, ushort Channel), Printed> _printed = new();
    private readonly TimeSpan _interval;
    private readonly int _hexBytes;
    private long _dropped;

    public ResultPrinter(TimeSpan interval, int hexBytes)
    {
        _interval = interval;
        _hexBytes = hexBytes;
    }

    public long Dropped => Interlocked.Read(ref _dropped);

    public void Push(in Sample sample)
    {
        if (!_queue.Writer.TryWrite(sample))
        {
            Interlocked.Increment(ref _dropped);
        }
    }

    public async Task RunAsync(CancellationToken token)
    {
        try
        {
            await foreach (var sample in _queue.Reader.ReadAllAsync(token))
            {
                if (ShouldPrint(sample))
                {
                    Console.WriteLine(Format(sample) + FormatHex(sample.Hex));
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private bool ShouldPrint(in Sample sample)
    {
        var key = (sample.Device, sample.Address, sample.Channel);
        if (_printed.TryGetValue(key, out var last)
            && last.Status == sample.Status
            && sample.Time - last.Time < _interval)
        {
            return false;
        }

        _printed[key] = new Printed(sample.Time, sample.Status);
        return true;
    }

    private static string Format(in Sample sample)
    {
        var echoTime = sample.EchoTime.HasValue ? $"{sample.EchoTime.Value:F1}ns" : "--";
        var deltaTof = sample.DeltaTof.HasValue ? $"{sample.DeltaTof.Value:F2}ns" : "--";
        var temp = sample.Temp.HasValue ? $"{sample.Temp.Value:F2}°C" : "--";

        var wave = sample.WavePoints > 0 ? $" 波形={sample.WavePoints}点" : string.Empty;

        return $"{sample.Time:HH:mm:ss.fff} {sample.Device} 地址{sample.Address}/通道{sample.Channel} " +
               $"声时={echoTime} 声时差={deltaTof} 温度={temp} 状态={Describe(sample.Status)}{wave}";
    }

    // HexString 是 "DE DE 01 ..." 这种带空格的写法，一个字节占 3 个字符
    private string FormatHex(string? hex)
    {
        if (_hexBytes <= 0 || string.IsNullOrEmpty(hex))
        {
            return string.Empty;
        }

        var total = (hex.Length + 1) / 3;
        if (_hexBytes < total)
        {
            return $" 报文{total}字节={hex[..(_hexBytes * 3 - 1)]}…";
        }

        return $" 报文{total}字节{Environment.NewLine}{Dump(hex, total)}";
    }

    private static string Dump(string hex, int total)
    {
        const int perLine = 32;
        var text = new StringBuilder();

        for (var offset = 0; offset < total; offset += perLine)
        {
            var count = Math.Min(perLine, total - offset);
            text.Append("    ")
                .Append(offset.ToString("X4"))
                .Append("  ")
                .Append(hex.AsSpan(offset * 3, count * 3 - 1));

            if (offset + perLine < total)
            {
                text.AppendLine();
            }
        }

        return text.ToString();
    }

    private static string Describe(MeasStatusCode status)
    {
        return status switch
        {
            MeasStatusCode.Normal => "正常",
            MeasStatusCode.SignalTooSmall => "信号太小",
            MeasStatusCode.ThresholdNotReached => "未达阈值",
            MeasStatusCode.SignalOverflow => "信号溢出",
            MeasStatusCode.SimilarityLow => "波形相似度低",
            MeasStatusCode.UnknownSignalError => "信号异常",
            MeasStatusCode.SnrLow => "信噪比低",
            MeasStatusCode.BelowLowerLimit => "低于下限",
            MeasStatusCode.AboveUpperLimit => "高于上限",
            _ => $"未知({(short)status})"
        };
    }

    private readonly record struct Printed(DateTime Time, MeasStatusCode Status);
}
