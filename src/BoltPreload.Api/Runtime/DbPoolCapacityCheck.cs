using Npgsql;

namespace BoltPreload.Api.Runtime;

/// <summary>
/// 连接池上限与 PostgreSQL <c>max_connections</c> 的核对。启动后查一次，结果进日志与 <c>/api/health/ready</c>。
/// <para>
/// 为什么值得单独检查：本进程在连接串没显式给池大小时会把上限抬到 200（48 通道满载估算），
/// 而 PostgreSQL 默认只有 100。两边错配的表现极具迷惑性——池耗尽时入库超时、进降级、往 WAL 堆，
/// 看上去和"数据库挂了"一模一样，排障会顺着数据库方向查很久，实际上是自己人挤自己人。
/// </para>
/// <para>
/// 这是配置问题不是运行时故障，所以只报 warning、不进 degradeReasons：装完就 503 会逼着人去关检查，
/// 那还不如不做。真正的防线是部署清单里那条核对项，这里负责让它无处可藏。
/// </para>
/// </summary>
public sealed class DbPoolCapacityCheck
{
    /// <summary>服务端连接数的安全占用比例。留出的余量给运维的 psql、备份任务、pgAdmin 以及超级用户预留连接。</summary>
    private const double SafeUtilization = 0.8;

    /// <summary>本进程连接池上限（来自连接串或代码默认值）。</summary>
    public int PoolMaxSize { get; }

    /// <summary>服务端 max_connections。未查到（库不通/无权限）为 null。</summary>
    public int? ServerMaxConnections { get; private set; }

    /// <summary>是否已完成核对。</summary>
    public bool Completed { get; private set; }

    public DbPoolCapacityCheck(int poolMaxSize) => PoolMaxSize = poolMaxSize;

    /// <summary>池上限吃掉了服务端容量的 80% 以上。</summary>
    public bool Mismatch => ServerMaxConnections is int m && m > 0 && PoolMaxSize > m * SafeUtilization;

    /// <summary>核对结论，供 /api/health/ready 展示；一切正常返回 null。</summary>
    public string? Warning => Mismatch
        ? $"连接池上限 {PoolMaxSize} 超过 PostgreSQL max_connections={ServerMaxConnections} 的 " +
          $"{SafeUtilization:P0}：高峰会连不上库，表现和数据库故障一样（入库超时、进降级、往 WAL 堆）。" +
          "调大服务端 max_connections，或在连接串写 Maximum Pool Size 收窄本进程。"
        : null;

    /// <summary>启动时重试的次数与间隔。服务可能比 PostgreSQL 先起来（NSSM 的依赖只保证启动顺序，
    /// 不保证对方已经能接受连接），一次查不到就放弃的话，这个核对在最需要它的机器上反而永远不做。</summary>
    private const int MaxAttempts = 5;
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(30);

    /// <summary>查服务端 max_connections 并核对，查不到就隔一会儿再试。失败不抛——核对不成功不该拦住启动。</summary>
    public async Task ProbeAsync(NpgsqlDataSource dataSource, ILogger logger, CancellationToken ct = default)
    {
        for (var attempt = 1; attempt <= MaxAttempts && !ct.IsCancellationRequested; attempt++)
        {
            try
            {
                await using var cmd = dataSource.CreateCommand("SHOW max_connections");
                var raw = await cmd.ExecuteScalarAsync(ct);
                if (raw is not null && int.TryParse(raw.ToString(), out var serverMax))
                {
                    ServerMaxConnections = serverMax;
                    break;
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "读取 PostgreSQL max_connections 失败（第 {Attempt}/{Max} 次）", attempt, MaxAttempts);
            }

            if (attempt < MaxAttempts)
            {
                try { await Task.Delay(RetryDelay, ct); }
                catch (OperationCanceledException) { return; }
            }
        }

        Completed = true;

        if (Warning is { } w)
            logger.LogError("连接池容量核对不通过：{Warning}", w);
        else if (ServerMaxConnections is int m)
            logger.LogInformation("连接池容量核对通过：池上限 {Pool} / 服务端 max_connections {Server}", PoolMaxSize, m);
        else
            logger.LogWarning("连接池容量核对未完成：{Max} 次都没读到 PostgreSQL 的 max_connections。" +
                "本进程池上限 {Pool}，请人工核对服务端容量是否够（错配的表现和数据库故障一样）。", MaxAttempts, PoolMaxSize);
    }
}
