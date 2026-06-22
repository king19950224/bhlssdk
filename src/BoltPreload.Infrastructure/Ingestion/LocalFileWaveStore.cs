using System.Globalization;
using System.Linq;
using System.Text.Json;
using BoltPreload.Application.Ingestion;
using BoltPreload.Fx.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BoltPreload.Infrastructure.Ingestion;

public sealed class LocalFileWaveStoreOptions
{
    /// <summary>波形文件根目录。建议绝对路径，挂到工业控制器的独立分区（防止系统盘满）。</summary>
    public string RootPath { get; set; } = "data/waves";

    /// <summary>单文件大小上限（字节）。超过则截断 WaveData 并警告（防止异常脏数据撑爆磁盘）。</summary>
    public long MaxFileBytes { get; set; } = 1 * 1024 * 1024;  // 1MB（典型波形 ~500 点 × 2 字节 + JSON 包装 ~3KB）

    /// <summary>截断后保留的最小点数（避免截断后只剩几十个点没有诊断价值）。</summary>
    public int MinPointsAfterTruncate { get; set; } = 256;

    /// <summary>波形文件保留天数。早于此的按日归档目录会被周期清理（防止磁盘无限增长）。&lt;=0 表示不清理。</summary>
    public int RetentionDays { get; set; } = 30;

    /// <summary>清理周期（小时）。波形是辅助诊断数据，低频清理即可。</summary>
    public int CleanupIntervalHours { get; set; } = 24;
}

/// <summary>
/// 本地文件波形存储。
/// <para>路径规则：<c>{RootPath}/{deviceId}/{channelNo}/{yyyyMMdd}/{tsUnixMs}.json</c></para>
/// <para>按日分目录，便于按时间清理 + 列表查询（GB/T 17626 要求历史数据归档可追溯）。</para>
/// <para>写操作走"写临时文件 + 原子 rename"，保证断电不留下半截 JSON。</para>
/// </summary>
public sealed class LocalFileWaveStore : IWaveStore
{
    private readonly LocalFileWaveStoreOptions _opt;
    private readonly ILogger<LocalFileWaveStore> _logger;
    private readonly string _rootAbs;
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public LocalFileWaveStore(IOptions<LocalFileWaveStoreOptions> opt, ILogger<LocalFileWaveStore> logger)
    {
        _opt = opt.Value;
        _logger = logger;
        Directory.CreateDirectory(_opt.RootPath);
        // 缓存"标准化后的根路径"，后续所有路径检查都和这个比对（防止 .. / 符号链接逃逸）
        _rootAbs = Path.GetFullPath(_opt.RootPath);
        if (!_rootAbs.EndsWith(Path.DirectorySeparatorChar))
            _rootAbs += Path.DirectorySeparatorChar;
    }

    public async Task<string> SaveAsync(
        long deviceId, ushort channelNo, DateTimeOffset ts, FxEchoWaveDto wave, CancellationToken ct = default)
    {
        var dayDir = ts.UtcDateTime.ToString("yyyyMMdd");
        // 文件名带短 GUID 唯一化：同毫秒同点数的两条测量不会再相互覆盖（旧命名 {ms}_{pts}.json 会 File.Move overwrite）
        var uniq = Guid.NewGuid().ToString("N")[..8];
        var fileName = $"{ts.ToUnixTimeMilliseconds()}_{wave.WaveData.Length}_{uniq}.json";

        var relPath = Path.Combine(deviceId.ToString(), channelNo.ToString(), dayDir, fileName);
        var absPath = Path.Combine(_opt.RootPath, relPath);

        Directory.CreateDirectory(Path.GetDirectoryName(absPath)!);

        var json = SerializePayload(deviceId, channelNo, ts, wave, truncated: false);

        if (json.Length > _opt.MaxFileBytes)
        {
            // 估算每点的字节开销（保留固定 JSON 包装），按比例推导截断后点数
            var headroom = Math.Max(0, _opt.MaxFileBytes - 512);  // 512B 给 JSON 头开销
            var bytesPerPoint = Math.Max(1, json.Length / Math.Max(1, wave.WaveData.Length));
            var targetPoints = Math.Max(_opt.MinPointsAfterTruncate, (int)(headroom / (bytesPerPoint + 1)));
            if (targetPoints >= wave.WaveData.Length)
                targetPoints = wave.WaveData.Length;  // 兜底：估错时不要截掉所有点

            _logger.LogWarning(
                "波形文件超过上限 device={DeviceId} ch={Channel} size={Size}B > {Max}B，截断到 {Pts} 点（原 {OrigPts}）",
                deviceId, channelNo, json.Length, _opt.MaxFileBytes, targetPoints, wave.WaveData.Length);

            var truncatedWave = wave with { WaveData = wave.WaveData.AsSpan(0, targetPoints).ToArray() };
            json = SerializePayload(deviceId, channelNo, ts, truncatedWave, truncated: true);
        }

        // 原子写：先写 .tmp 再 rename。Rename 是 POSIX 原子操作，断电要么完整要么不存在。
        // 每次写用独立 .tmp（带 GUID），避免并发写共享同一个 .tmp 造成竞态/串扰。
        var tmpPath = $"{absPath}.{Guid.NewGuid():N}.tmp";
        await File.WriteAllBytesAsync(tmpPath, json, ct);
        File.Move(tmpPath, absPath, overwrite: true);

        return relPath.Replace('\\', '/');
    }

    private static byte[] SerializePayload(long deviceId, ushort channelNo, DateTimeOffset ts, FxEchoWaveDto wave, bool truncated)
    {
        var payload = new
        {
            deviceId,
            channelNo,
            ts = ts.UtcDateTime,
            wave.Fc,
            wave.ClockInt,
            wave.EchoStart,
            wave.Threshold,
            pointCount = wave.WaveData.Length,
            truncated,
            waveData = wave.WaveData
        };
        return JsonSerializer.SerializeToUtf8Bytes(payload, JsonOpts);
    }

    public async Task<byte[]?> ReadRawAsync(string relativePath, CancellationToken ct = default)
    {
        if (!TryResolveSafePath(relativePath, out var absPath))
        {
            _logger.LogWarning("非法波形路径 {Path}", relativePath);
            return null;
        }

        if (!File.Exists(absPath))
            return null;

        return await File.ReadAllBytesAsync(absPath, ct);
    }

    public Task<bool> DeleteAsync(string relativePath, CancellationToken ct = default)
    {
        if (!TryResolveSafePath(relativePath, out var absPath))
        {
            _logger.LogWarning("删除波形：非法路径 {Path}", relativePath);
            return Task.FromResult(false);
        }
        try
        {
            if (File.Exists(absPath))
            {
                File.Delete(absPath);
                return Task.FromResult(true);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "删除波形文件失败 {Path}", relativePath);
        }
        return Task.FromResult(false);
    }

    // 文件 IO 是同步 API，放到线程池跑，避免在后台清理时占用调用线程。
    public Task<int> PurgeOlderThanAsync(DateTimeOffset cutoff, IReadOnlySet<string> protectedRelativePaths, CancellationToken ct = default)
        => Task.Run(() => PurgeCore(cutoff, protectedRelativePaths, ct), ct);

    private int PurgeCore(DateTimeOffset cutoff, IReadOnlySet<string> protectedPaths, CancellationToken ct)
    {
        int deleted = 0;
        // 1. 清扫崩溃残留的 .tmp（成功写入会立即 rename，存活的 .tmp 一定是半截文件）
        deleted += CleanupTempFiles();

        if (!Directory.Exists(_rootAbs)) return deleted;

        // 2. 删除早于 cutoff 当天的归档：{root}/{deviceId}/{channelNo}/{yyyyMMdd}/
        //    逐文件删（而非整目录），以便跳过被保护的参考波形文件（参考基线可能比 cutoff 更老但仍在用）。
        var cutoffDay = cutoff.UtcDateTime.Date;
        foreach (var deviceDir in Directory.EnumerateDirectories(_rootAbs))
        {
            foreach (var channelDir in Directory.EnumerateDirectories(deviceDir))
            {
                foreach (var dayDir in Directory.EnumerateDirectories(channelDir))
                {
                    ct.ThrowIfCancellationRequested();
                    var name = Path.GetFileName(dayDir);
                    if (!DateTime.TryParseExact(name, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day))
                        continue;
                    if (day.Date >= cutoffDay) continue;
                    try
                    {
                        foreach (var file in Directory.EnumerateFiles(dayDir))
                        {
                            var rel = Path.GetRelativePath(_rootAbs, file).Replace('\\', '/');
                            if (protectedPaths.Contains(rel)) continue;  // 保护：参考波形文件不清
                            File.Delete(file);
                            deleted++;
                        }
                        // 目录清空后再删；仍含被保护文件则保留目录
                        if (!Directory.EnumerateFileSystemEntries(dayDir).Any())
                            Directory.Delete(dayDir);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "清理波形日目录失败 {Dir}", dayDir);
                    }
                }
            }
        }
        if (deleted > 0)
            _logger.LogInformation("波形清理完成：删除 {Count} 个文件（cutoff={Cutoff:yyyy-MM-dd}）", deleted, cutoffDay);
        return deleted;
    }

    /// <summary>清扫根目录下所有残留 .tmp（断电/崩溃在 rename 前留下的半截文件）。返回删除数。</summary>
    private int CleanupTempFiles()
    {
        int n = 0;
        try
        {
            if (!Directory.Exists(_rootAbs)) return 0;
            foreach (var tmp in Directory.EnumerateFiles(_rootAbs, "*.tmp", SearchOption.AllDirectories))
            {
                try { File.Delete(tmp); n++; }
                catch { /* best-effort，单个删失败不影响其它 */ }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "清扫残留 .tmp 失败");
        }
        return n;
    }

    /// <summary>
    /// 把相对路径解析成绝对路径，并校验最终绝对路径仍在 RootPath 下。
    /// 防御 ".." 逃逸、Windows drive-relative（"C:bad"）、绝对路径、符号链接出框等情况。
    /// </summary>
    private bool TryResolveSafePath(string relativePath, out string absPath)
    {
        absPath = "";
        if (string.IsNullOrWhiteSpace(relativePath)) return false;
        if (Path.IsPathRooted(relativePath)) return false;
        // 早期挡 ":" 防御 Windows drive-relative（GetFullPath 不会必然报错）
        if (relativePath.Contains(':')) return false;

        try
        {
            absPath = Path.GetFullPath(Path.Combine(_opt.RootPath, relativePath));
        }
        catch
        {
            return false;
        }

        return absPath.StartsWith(_rootAbs, StringComparison.Ordinal);
    }
}
