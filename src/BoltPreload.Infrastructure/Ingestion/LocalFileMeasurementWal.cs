using System.Text.Json;
using BoltPreload.Application.Ingestion;
using BoltPreload.Fx.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BoltPreload.Infrastructure.Ingestion;

/// <summary>
/// 基于本地目录-文件的测量 WAL：每条入库失败的信封 = 一个 JSON 文件。
/// <para>
/// 文件名 <c>{unixMillis:D13}_{seq:D9}.json</c>，按字典序即时间序，便于先进先出重放。
/// 落盘走 tmp→move 保证原子；重放成功即删文件；写入失败（DB 仍不可用）停止本轮保留剩余；
/// 损坏条目重命名为 <c>.corrupt</c> 隔离，避免堵塞队头。
/// </para>
/// </summary>
public sealed class LocalFileMeasurementWal : IMeasurementWal, IDisposable
{
    private const string FileExtension = ".json";
    private const string TmpSuffix = ".tmp";
    private const string CorruptSuffix = ".corrupt";
    private const int MaxReplayPerRound = 500;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    private readonly string _root;
    private readonly int _maxEntries;
    private readonly ILogger<LocalFileMeasurementWal> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private long _pending;
    private long _seq;
    private long _dropped;

    public LocalFileMeasurementWal(IOptions<MeasurementWalOptions> options, ILogger<LocalFileMeasurementWal> logger)
    {
        var opt = options.Value;
        _root = Path.GetFullPath(opt.RootPath);
        _maxEntries = Math.Max(1, opt.MaxEntries);
        _logger = logger;

        Directory.CreateDirectory(_root);
        CleanupLeftoverTempFiles();
        _pending = CountWalFiles();

        if (_pending > 0)
            _logger.LogWarning("WAL 启动发现 {Count} 条历史待重放条目 root={Root}", _pending, _root);
        else
            _logger.LogInformation("WAL 启动 root={Root} maxEntries={Max}", _root, _maxEntries);
    }

    public bool Enabled => true;

    public long PendingCount => Interlocked.Read(ref _pending);

    public async ValueTask AppendAsync(FxDecodedEnvelope envelope, CancellationToken cancellationToken = default)
    {
        // 仅持久化测量类信封；DeviceParameter 含运行时类型不可靠往返，不入 WAL
        if (envelope.ResultType is not (FxResultType.ChannelResult or FxResultType.MultiChannelResult))
            return;

        // WAL 的职责是断网期兜底补写测量“元数据”（声时/温度/状态码），不是波形归档：
        // 带波形的条目单条可达数百 KB，DB 故障窗口内会把磁盘和序列化 CPU 打爆——落盘前剥离波形。
        envelope = StripWaves(envelope);

        byte[] bytes;
        try
        {
            bytes = JsonSerializer.SerializeToUtf8Bytes(envelope, JsonOpts);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "WAL 序列化信封失败，丢弃 device={DeviceId} ts={Ts}", envelope.DeviceId, envelope.ReceiveTime);
            return;
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            EnforceCapacityLocked();

            var name = $"{envelope.IngestedAt.ToUnixTimeMilliseconds():D13}_{Interlocked.Increment(ref _seq):D9}{FileExtension}";
            var finalPath = Path.Combine(_root, name);
            var tmpPath = finalPath + TmpSuffix;

            await File.WriteAllBytesAsync(tmpPath, bytes, cancellationToken);
            File.Move(tmpPath, finalPath, overwrite: false);
            Interlocked.Increment(ref _pending);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<int> ReplayAsync(
        Func<FxDecodedEnvelope, CancellationToken, Task> writeAsync,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(writeAsync);

        await _gate.WaitAsync(cancellationToken);
        try
        {
            var files = ListWalFilesSorted();
            // 单轮限量：积压数万条时不无限持有 _gate（会把热路径 Append 卡死），剩余留给下一轮（15s 周期）
            if (files.Count > MaxReplayPerRound)
                files = files.Take(MaxReplayPerRound).ToList();
            var replayed = 0;

            for (var i = 0; i < files.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var path = files[i];

                FxDecodedEnvelope? envelope;
                try
                {
                    var bytes = await File.ReadAllBytesAsync(path, cancellationToken);
                    envelope = JsonSerializer.Deserialize<FxDecodedEnvelope>(bytes, JsonOpts);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "WAL 条目损坏无法反序列化，隔离 file={File}", Path.GetFileName(path));
                    Quarantine(path);
                    continue;
                }

                if (envelope is null)
                {
                    _logger.LogWarning("WAL 条目反序列化为空，隔离 file={File}", Path.GetFileName(path));
                    Quarantine(path);
                    continue;
                }

                try
                {
                    await writeAsync(envelope, cancellationToken);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    // 写入失败：DB 大概率仍不可用，停止本轮，保留这条与后续条目下轮再试
                    _logger.LogWarning(ex, "WAL 重放写入失败，暂停本轮（剩余约 {Remaining} 条待下轮）file={File}",
                        files.Count - i, Path.GetFileName(path));
                    break;
                }

                TryDelete(path);
                replayed++;
            }

            // 以磁盘实际剩余为准，避免计数漂移
            Interlocked.Exchange(ref _pending, CountWalFiles());
            return replayed;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>容量保护：达到上限则丢弃最旧条目（drop-oldest），给即将写入的新条目腾位。调用方需持有 _gate。</summary>
    private void EnforceCapacityLocked()
    {
        // L2 快路径：未接近上限时只看内存计数，不枚举整个目录——避免每条 append 都 O(n) 扫描磁盘
        //（DB 长时间故障、WAL 积压数万条时性能尤其关键）。仅在计数触及上限时才枚举做 drop-oldest（罕见）。
        if (Interlocked.Read(ref _pending) < _maxEntries)
            return;

        var files = ListWalFilesSorted();
        if (files.Count < _maxEntries)
        {
            Interlocked.Exchange(ref _pending, files.Count);
            return;
        }

        var target = _maxEntries - 1; // 留 1 个位置给新条目
        var dropCount = files.Count - target;
        for (var i = 0; i < dropCount && i < files.Count; i++)
            TryDelete(files[i]);

        var total = Interlocked.Add(ref _dropped, dropCount);
        _logger.LogWarning("WAL 达到上限 {Max}，drop-oldest 丢弃 {Drop} 条最旧测量（累计丢弃 {Total}）—— 排查 DB 不可用时长",
            _maxEntries, dropCount, total);
        Interlocked.Exchange(ref _pending, target);
    }

    private List<string> ListWalFilesSorted()
    {
        var files = Directory.EnumerateFiles(_root)
            .Where(f => f.EndsWith(FileExtension, StringComparison.Ordinal))
            .ToList();
        files.Sort(static (a, b) => string.CompareOrdinal(Path.GetFileName(a), Path.GetFileName(b)));
        return files;
    }

    private int CountWalFiles()
        => Directory.EnumerateFiles(_root).Count(f => f.EndsWith(FileExtension, StringComparison.Ordinal));

    private void CleanupLeftoverTempFiles()
    {
        foreach (var tmp in Directory.EnumerateFiles(_root).Where(f => f.EndsWith(TmpSuffix, StringComparison.Ordinal)))
            TryDelete(tmp);
    }

    private void Quarantine(string path)
    {
        try
        {
            File.Move(path, path + CorruptSuffix, overwrite: true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "隔离损坏 WAL 条目失败，改为删除 file={File}", Path.GetFileName(path));
            TryDelete(path);
        }
    }

    private void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "删除 WAL 条目失败 file={File}", Path.GetFileName(path));
        }
    }

    private static FxDecodedEnvelope StripWaves(FxDecodedEnvelope envelope)
    {
        if (envelope.ChannelResult?.EchoWave is not null)
            envelope = envelope with { ChannelResult = envelope.ChannelResult with { EchoWave = null } };
        var mc = envelope.MultiChannelResult;
        if (mc is not null && mc.Channels.Values.Any(c => c.EchoWave is not null))
        {
            var stripped = mc.Channels.ToDictionary(
                kv => kv.Key,
                kv => kv.Value.EchoWave is null ? kv.Value : kv.Value with { EchoWave = null });
            envelope = envelope with { MultiChannelResult = new FxMultiChannelResultDto(stripped) };
        }
        return envelope;
    }

    public void Dispose() => _gate.Dispose();
}
