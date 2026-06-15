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
        var fileName = $"{ts.ToUnixTimeMilliseconds()}_{wave.WaveData.Length}.json";

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
        var tmpPath = absPath + ".tmp";
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
