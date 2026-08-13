using BoltPreload.Application.Diagnostics;
using BoltPreload.Application.Ingestion;
using BoltPreload.Infrastructure.Ingestion;
using Microsoft.Extensions.Options;

namespace BoltPreload.Api.Runtime;

/// <summary>
/// 系统级故障的统一判定口径。<c>/api/health/ready</c> 与 <see cref="SystemAlertService"/> 共用这一份，
/// 避免出现"界面横幅说降级、外发通知没动静"这种两套口径打架的情况。
/// <para>
/// 只看进程内存状态与本地磁盘，不查库：库挂的时候这里还得出得了结论，再去查库就把自己也搭进去了。
/// DB 连通性由调用方自己探完传进来（<c>dbDown</c>）。
/// </para>
/// </summary>
public sealed class SystemHealthEvaluator
{
    private readonly IngestDegradeState _degrade;
    private readonly BackgroundServiceHealth _svcHealth;
    private readonly HealthThresholdOptions _options;
    private readonly string[] _watchedPaths;

    public SystemHealthEvaluator(
        IngestDegradeState degrade,
        BackgroundServiceHealth svcHealth,
        IOptions<HealthThresholdOptions> options,
        IOptions<MeasurementWalOptions> walOptions,
        IOptions<LocalFileWaveStoreOptions> waveOptions)
    {
        _degrade = degrade;
        _svcHealth = svcHealth;
        _options = options.Value;
        // 要看的不止程序盘。部署手册建议把 WAL 与波形挪到独立数据分区，只看程序盘的话，
        // 数据盘写满了这里一无所知——而写满的恰恰多半是数据盘，程序盘上就那么点东西。
        _watchedPaths = [AppContext.BaseDirectory, walOptions.Value.RootPath, waveOptions.Value.RootPath];
    }

    /// <summary>当前配置的磁盘余量下限（GB）。</summary>
    public double MinFreeDiskGb => _options.MinFreeDiskGb;

    /// <param name="dbDown">调用方已探明数据库不通。探针会传，告警服务也会传。</param>
    /// <param name="serviceSnapshot">
    /// 已经取过的后台服务快照。探针要在返回体里同时给明细和失联名单，
    /// 各取一次快照的话两个字段会对不上，让人以为哪边算错了。
    /// </param>
    public SystemHealthVerdict Evaluate(
        bool dbDown = false,
        IReadOnlyList<BackgroundServiceState>? serviceSnapshot = null)
    {
        var (freeDiskGb, tightestRoot) = ProbeFreeDiskGb(_watchedPaths);

        var reasons = new List<string>();
        if (dbDown) reasons.Add("db_down");
        if (_degrade.DbWriteDegraded) reasons.Add("db_write_failing");
        if (_degrade.WalDroppedRecently) reasons.Add("wal_saturated");
        // 磁盘：WAL、波形、日志全在本地盘。此前这个值只在 readiness 里当展示字段挂着，
        // 盘写满之前探针一路绿灯，等到写库降级才间接暴露——中间那段时间数据已经在悄悄丢了。
        if (freeDiskGb is not null && _options.MinFreeDiskGb > 0 && freeDiskGb < _options.MinFreeDiskGb)
            reasons.Add("low_disk");

        var dead = (serviceSnapshot ?? _svcHealth.Snapshot())
            .Where(s => s.Stale).Select(s => s.Name).ToArray();
        return new SystemHealthVerdict(reasons, dead, freeDiskGb, tightestRoot);
    }

    /// <summary>
    /// 探这几个路径所在盘的可用空间，返回其中最紧张的那个（GB）与它的盘符。
    /// 路径可以是相对的，也可以还不存在（首次启动时 data 目录未建），按盘根算即可。
    /// 全都探不到返回 null，不让探测失败挡住整个判定。
    /// </summary>
    public static (double? FreeGb, string? Root) ProbeFreeDiskGb(IEnumerable<string> paths)
    {
        double? min = null;
        string? minRoot = null;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var path in paths)
        {
            if (string.IsNullOrWhiteSpace(path)) continue;
            try
            {
                var root = Path.GetPathRoot(Path.GetFullPath(path));
                if (string.IsNullOrEmpty(root) || !seen.Add(root)) continue;
                var free = Math.Round(new DriveInfo(root).AvailableFreeSpace / 1024.0 / 1024 / 1024, 1);
                if (min is null || free < min)
                {
                    min = free;
                    minRoot = root;
                }
            }
            catch
            {
                // 单个路径探不到（盘符不存在、无权限）不影响其余判定
            }
        }
        return (min, minRoot);
    }
}

/// <summary>一次系统级健康判定的结果。</summary>
/// <param name="DegradeReasons">降级理由代码，如 db_down / db_write_failing / wal_saturated / low_disk。</param>
/// <param name="DeadServices">超过 3 倍正常间隔未心跳的后台服务名。</param>
/// <param name="FreeDiskGb">程序、WAL、波形三处所在盘中最紧张的可用空间（GB），全探不到为 null。</param>
/// <param name="TightestDiskRoot">上面那个数字来自哪个盘，排障时不用猜是系统盘还是数据盘。</param>
public sealed record SystemHealthVerdict(
    IReadOnlyList<string> DegradeReasons,
    IReadOnlyList<string> DeadServices,
    double? FreeDiskGb,
    string? TightestDiskRoot = null)
{
    /// <summary>没有任何降级理由、也没有失联服务。</summary>
    public bool Healthy => DegradeReasons.Count == 0 && DeadServices.Count == 0;

    /// <summary>把全部问题拼成一个稳定的指纹，用于判断"故障面变了没有"（顺序无关）。</summary>
    public string Fingerprint()
    {
        var all = DegradeReasons.Concat(DeadServices.Select(s => "dead:" + s)).OrderBy(s => s, StringComparer.Ordinal);
        return string.Join('|', all);
    }
}

/// <summary>健康判定阈值（配置节 <c>Health</c>）。</summary>
public sealed class HealthThresholdOptions
{
    /// <summary>配置节名。</summary>
    public const string SectionName = "Health";

    /// <summary>
    /// 磁盘可用空间下限（GB），低于此值判 low_disk 降级。
    /// 判定看的是程序目录、WAL 目录、波形目录三处所在盘里最紧张的那个——手册建议把后两者
    /// 挪到独立数据分区，只盯程序盘会漏掉真正会先写满的那块。
    /// 默认 5 GB：够 WAL 满载（5 万条剥掉波形约百 MB 级）加几天日志加波形归档的缓冲，
    /// 留出运维赶到现场清盘的时间。≤0 关闭这项判定。
    /// </summary>
    public double MinFreeDiskGb { get; set; } = 5.0;
}
