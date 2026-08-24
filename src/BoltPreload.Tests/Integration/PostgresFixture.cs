using BoltPreload.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Xunit;

namespace BoltPreload.Tests.Integration;

/// <summary>
/// 数据库集成测试夹具：在 TimescaleDB 上建一个独立测试库，按 <c>deploy/sql/0001..00NN</c> 顺序建 schema，
/// 测试结束删库。无可用数据库时（本地无 Docker / CI 未起库）标记 <see cref="Available"/>=false，
/// 相关测试自动 Skip，不影响纯单元测试。
/// <para>连接串：环境变量 <c>ConnectionStrings__Postgres</c>（CI 用），否则回退本地 dev 默认。</para>
/// <para>
/// 库名带进程号：曾用固定名 <c>boltpreload_inttest</c> + "先删后建"，任何并发的第二个测试进程
/// （IDE 测试运行器的发现/运行、被打断遗留的孤儿 testhost、并行的命令行运行）初始化夹具时会把
/// 正在使用的库 <c>DROP WITH (FORCE)</c>——在跑的那轮立刻雪崩成大片 57P01/3D000 假失败
/// （2026-08-17 用 pg_stat_activity 抓到现行：两个会话相隔 5 秒互删互建）。
/// 每进程独立库名从结构上消除相撞；孤儿进程留下的陈旧库在下次初始化时按"无活动连接"判据顺手清掉。
/// </para>
/// </summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    private const string TestDbPrefix = "boltpreload_inttest";
    private readonly string _testDb = $"{TestDbPrefix}_{Environment.ProcessId}";
    private string _adminConn = "";

    public bool Available { get; private set; }
    public string? SkipReason { get; private set; }
    public string ConnectionString { get; private set; } = "";
    public NpgsqlDataSource DataSource { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        var baseConn = PostgresTestConnection.BaseConnectionString;
        try
        {
            _adminConn = new NpgsqlConnectionStringBuilder(baseConn) { Database = "postgres" }.ConnectionString;
            ConnectionString = new NpgsqlConnectionStringBuilder(baseConn) { Database = _testDb }.ConnectionString;

            // 独立测试库：先删后建，保证每次跑都是干净 schema
            await using (var admin = new NpgsqlConnection(_adminConn))
            {
                await admin.OpenAsync();

                // 顺手清理孤儿测试库：进程被杀时 DisposeAsync 不会跑，库会留下。
                // "无活动连接"= 没有任何在跑的夹具持有它（活跃夹具的连接池整轮都挂着连接），可安全删。
                await using (var stale = admin.CreateCommand())
                {
                    stale.CommandText = @"
                        SELECT datname FROM pg_database d
                        WHERE datname LIKE @prefix || '_%' AND datname <> @mine
                          AND NOT EXISTS (SELECT 1 FROM pg_stat_activity a WHERE a.datname = d.datname)";
                    stale.Parameters.AddWithValue("prefix", TestDbPrefix);
                    stale.Parameters.AddWithValue("mine", _testDb);
                    var staleDbs = new List<string>();
                    await using (var r = await stale.ExecuteReaderAsync())
                        while (await r.ReadAsync()) staleDbs.Add(r.GetString(0));
                    foreach (var db in staleDbs)
                    {
                        try { await ExecAsync(admin, $"DROP DATABASE IF EXISTS \"{db}\""); }
                        catch { /* 另一进程刚好在用/已删，无关紧要 */ }
                    }
                }
                // 兼容清理曾经的固定名残留（升级过渡期）
                try { await ExecAsync(admin, $"DROP DATABASE IF EXISTS {TestDbPrefix}"); }
                catch { /* 有连接说明旧版本进程在用，跳过 */ }

                await ExecAsync(admin, $"DROP DATABASE IF EXISTS \"{_testDb}\" WITH (FORCE)");
                await ExecAsync(admin, $"CREATE DATABASE \"{_testDb}\"");
            }

            // 按文件名序号顺序执行迁移（与 docker-compose initdb / 工控机 Inno Setup 同一套脚本）
            await using (var conn = new NpgsqlConnection(ConnectionString))
            {
                await conn.OpenAsync();
                foreach (var path in MigrationScripts())
                    await ExecAsync(conn, await File.ReadAllTextAsync(path));
            }

            DataSource = new NpgsqlDataSourceBuilder(ConnectionString).Build();
            Available = true;
        }
        catch (Exception ex)
        {
            Available = false;
            SkipReason = $"Postgres 不可用，跳过 DB 集成测试：{ex.Message}";
        }
    }

    public async Task DisposeAsync()
    {
        if (DataSource is not null) await DataSource.DisposeAsync();
        if (!Available) return;
        try
        {
            await using var admin = new NpgsqlConnection(_adminConn);
            await admin.OpenAsync();
            await ExecAsync(admin, $"DROP DATABASE IF EXISTS \"{_testDb}\" WITH (FORCE)");
        }
        catch { /* best effort 清理；进程被杀时留下的库由下次初始化的孤儿清理兜底 */ }
    }

    /// <summary>
    /// 断言夹具已就绪。库连不上的情况由 <see cref="PostgresFactAttribute"/> 在发现期跳过，
    /// 走到这里说明库是通的却仍然初始化失败（建库被拒、迁移脚本报错等），那是真故障，得红，
    /// 不能像以前那样 <c>return</c> 掉装作通过。
    /// </summary>
    public void EnsureAvailable()
    {
        if (!Available)
            throw new InvalidOperationException(
                $"数据库可连但测试夹具初始化失败（建库/迁移出错），这是真实故障：{SkipReason}");
    }

    public BoltPreloadDbContext NewDbContext()
    {
        var opts = new DbContextOptionsBuilder<BoltPreloadDbContext>().UseNpgsql(ConnectionString).Options;
        return new BoltPreloadDbContext(opts);
    }

    private static async Task ExecAsync(NpgsqlConnection conn, string sql)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }

    // 仅取 deploy/sql 顶层的 0001..00NN（不含 dev/ 与 maintenance/ 子目录），按文件名字典序执行
    private static IEnumerable<string> MigrationScripts()
    {
        var sqlDir = FindSqlDir();
        return Directory.GetFiles(sqlDir, "0*.sql")
            .OrderBy(f => Path.GetFileName(f), StringComparer.Ordinal);
    }

    private static string FindSqlDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "deploy", "sql");
            if (File.Exists(Path.Combine(candidate, "0001_initial_schema.sql")))
                return candidate;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("从测试程序集向上未找到 deploy/sql 目录");
    }
}

/// <summary>所有 DB 集成测试共享一个夹具（建库/迁移开销大，只做一次）。</summary>
[CollectionDefinition("postgres")]
public sealed class PostgresCollection : ICollectionFixture<PostgresFixture>;
