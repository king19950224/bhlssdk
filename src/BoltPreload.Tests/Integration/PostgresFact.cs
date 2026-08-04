using Npgsql;
using Xunit;

namespace BoltPreload.Tests.Integration;

/// <summary>
/// 数据库集成测试的连接串口径（夹具与发现期探测共用，避免两处写法漂移）。
/// </summary>
internal static class PostgresTestConnection
{
    /// <summary>CI 用环境变量注入；否则回退本地 dev 库默认值。</summary>
    public static string BaseConnectionString =>
        Environment.GetEnvironmentVariable("ConnectionStrings__Postgres")
        ?? "Host=localhost;Port=5432;Database=boltpreload;Username=boltpreload;Password=dev_only_NOT_for_production";
}

/// <summary>
/// 发现期探测数据库是否可连，结果进程内缓存一次。
/// <para>
/// 为什么要在发现期探测：xUnit v2 只认 <see cref="FactAttribute.Skip"/>（发现期求值），
/// 没有运行期动态跳过（<c>Assert.Skip</c> 是 v3 API；v2 的 <c>DynamicSkipToken</c> 是 internal
/// 且当前执行引擎不认，实测会记成 Failed 而不是 Skipped）。
/// </para>
/// </summary>
internal static class PostgresAvailability
{
    private static readonly Lazy<(bool Ok, string Reason)> Probe = new(() =>
    {
        try
        {
            // 只连 postgres 维护库、超时压到 3s：发现期不能因为没起库而把整个测试发现拖慢或拖崩。
            var conn = new NpgsqlConnectionStringBuilder(PostgresTestConnection.BaseConnectionString)
            {
                Database = "postgres",
                Timeout = 3,
                CommandTimeout = 3
            }.ConnectionString;

            using var c = new NpgsqlConnection(conn);
            c.Open();
            return (true, "");
        }
        catch (Exception ex)
        {
            return (false, $"Postgres 不可用，跳过 DB 集成测试：{ex.Message}");
        }
    }, LazyThreadSafetyMode.ExecutionAndPublication);

    public static bool IsAvailable => Probe.Value.Ok;
    public static string SkipReason => Probe.Value.Reason;
}

/// <summary>
/// 需要真实 PostgreSQL/TimescaleDB 的测试。无可用数据库时报告为 <b>Skipped</b>。
/// <para>
/// 这批测试原先写成 <c>if (!_fx.Available) return;</c> —— 提前 return 被 xUnit 记为"已通过"，
/// 于是在没起库的机器上能跑出"142/142 全绿"，而 32 个 DB 集成测试（EF 与手写 schema 是否漂移、
/// WAL 重放是否真落库、权限护栏是否真拦）一条断言都没执行。交付前回归最依赖的正是这批测试，
/// 静默通过比失败更危险，因此改为显式 Skipped：结果里能一眼看出"没跑"。
/// </para>
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1813:避免使用未密封的特性",
    Justification = "需要继承 FactAttribute；本类型自身已 sealed。")]
public sealed class PostgresFactAttribute : FactAttribute
{
    public PostgresFactAttribute()
    {
        if (!PostgresAvailability.IsAvailable)
            Skip = PostgresAvailability.SkipReason;
    }
}

/// <summary>
/// <see cref="PostgresFactAttribute"/> 的参数化版本，用于一个场景要拿多组输入各跑一遍的情况
/// （如同一个端点对多种攻击形状的路径）。跳过口径与 Fact 版完全一致。
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1813:避免使用未密封的特性",
    Justification = "需要继承 TheoryAttribute；本类型自身已 sealed。")]
public sealed class PostgresTheoryAttribute : TheoryAttribute
{
    public PostgresTheoryAttribute()
    {
        if (!PostgresAvailability.IsAvailable)
            Skip = PostgresAvailability.SkipReason;
    }
}
