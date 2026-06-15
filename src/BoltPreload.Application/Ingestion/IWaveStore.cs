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
}
