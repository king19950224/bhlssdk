using BoltPreload.Fx.Models;

namespace BoltPreload.Application.Ingestion;

/// <summary>
/// 波形存储抽象。
/// <para>
/// Phase 1 用 <c>LocalFileWaveStore</c>（本地文件）。Phase 3 可换 S3 / MinIO / 阿里云 OSS 实现。
/// 不直接走数据库 LOB —— PostgreSQL bytea 写大文件会显著拖慢热路径。
/// </para>
/// </summary>
public interface IWaveStore
{
    /// <summary>
    /// 保存一个 EchoWave，返回相对路径（写到 <c>measurements.wave_blob_url</c>）。
    /// 调用者应捕获异常，存储失败不应阻塞主测量入库。
    /// </summary>
    Task<string> SaveAsync(long deviceId, ushort channelNo, DateTimeOffset ts, FxEchoWaveDto wave, CancellationToken ct = default);

    /// <summary>
    /// 按相对路径读取保存的波形 JSON。null 表示文件不存在。
    /// </summary>
    Task<byte[]?> ReadRawAsync(string relativePath, CancellationToken ct = default);

    /// <summary>
    /// 删除某个相对路径的波形文件（路径非法或不存在时返回 false，不抛）。
    /// 用于入库冲突时清理"已写文件但 measurement 未插入"的孤儿波形。
    /// </summary>
    Task<bool> DeleteAsync(string relativePath, CancellationToken ct = default);

    /// <summary>
    /// 清理早于 cutoff 的历史波形并清扫崩溃残留的临时文件，返回删除的文件数。
    /// <paramref name="protectedRelativePaths"/> 中的文件（如仍在用的参考波形，可能比 cutoff 更老）会被跳过不删。
    /// 波形是辅助诊断数据，无限增长会撑爆磁盘，必须有 retention。
    /// </summary>
    Task<int> PurgeOlderThanAsync(DateTimeOffset cutoff, IReadOnlySet<string> protectedRelativePaths, CancellationToken ct = default);
}
