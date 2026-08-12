using System.Text;
using System.Threading.Channels;
using FxSDK.Models;

namespace FxReader;

readonly record struct RawFrame(
    string Device,
    DateTime Time,
    ushort Address,
    ushort Channel,
    MeasStatusCode Status,
    decimal? EchoTime,
    decimal? DeltaTof,
    decimal? RawTemp,
    decimal? SpTemp,
    uint WaveStart,
    ushort ClockInt,
    IReadOnlyList<short>? Wave,
    string? Hex);

// 按天一个 CSV，波形点和原始报文都整帧落盘
sealed class RawWriter
{
    private const string Header =
        "时间,设备,地址,通道,状态,声时ns,声时差ns,原始温度,平滑温度,波形起点ns,点间隔ns,点数,波形,原始报文";

    private readonly Channel<RawFrame> _queue = Channel.CreateBounded<RawFrame>(
        new BoundedChannelOptions(2048)
        {
            SingleReader = true,
            FullMode = BoundedChannelFullMode.Wait
        });

    private readonly string _directory;
    private long _dropped;
    private long _written;

    public RawWriter(string directory)
    {
        _directory = directory;
    }

    public string Directory => _directory;

    public long Dropped => Interlocked.Read(ref _dropped);

    public long Written => Interlocked.Read(ref _written);

    public void Push(in RawFrame frame)
    {
        if (!_queue.Writer.TryWrite(frame))
        {
            Interlocked.Increment(ref _dropped);
        }
    }

    public async Task RunAsync(CancellationToken token)
    {
        System.IO.Directory.CreateDirectory(_directory);

        StreamWriter? writer = null;
        var day = DateTime.MinValue;

        try
        {
            await foreach (var frame in _queue.Reader.ReadAllAsync(token))
            {
                if (writer is null || frame.Time.Date != day)
                {
                    writer?.Dispose();
                    day = frame.Time.Date;

                    var path = Path.Combine(_directory, $"raw-{day:yyyyMMdd}.csv");
                    var isNew = !File.Exists(path);
                    writer = new StreamWriter(path, append: true, Encoding.UTF8) { AutoFlush = true };
                    if (isNew)
                    {
                        await writer.WriteLineAsync(Header);
                    }
                }

                await writer.WriteLineAsync(Format(frame));
                Interlocked.Increment(ref _written);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Console.WriteLine($"原始数据写入失败：{ex.Message}");
        }
        finally
        {
            writer?.Dispose();
        }
    }

    private static string Format(in RawFrame frame)
    {
        var wave = frame.Wave is { Count: > 0 } ? string.Join(' ', frame.Wave) : string.Empty;

        return string.Join(',',
            frame.Time.ToString("yyyy-MM-dd HH:mm:ss.fff"),
            Quote(frame.Device),
            frame.Address,
            frame.Channel,
            (short)frame.Status,
            frame.EchoTime,
            frame.DeltaTof,
            frame.RawTemp,
            frame.SpTemp,
            frame.WaveStart,
            frame.ClockInt,
            frame.Wave?.Count ?? 0,
            Quote(wave),
            frame.Hex ?? string.Empty);
    }

    private static string Quote(string value)
    {
        return $"\"{value.Replace("\"", "\"\"")}\"";
    }
}
