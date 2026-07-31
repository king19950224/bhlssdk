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
/// </summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    private const string TestDb = "boltpreload_inttest";
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
            ConnectionString = new NpgsqlConnectionStringBuilder(baseConn) { Database = TestDb }.ConnectionString;

            // 独立测试库：先删后建，保证每次跑都是干净 schema
            await using (var admin = new NpgsqlConnection(_adminConn))
            {
                await admin.OpenAsync();
                await ExecAsync(admin, $"DROP DATABASE IF EXISTS {TestDb} WITH (FORCE)");
                await ExecAsync(admin, $"CREATE DATABASE {TestDb}");
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
            await ExecAsync(admin, $"DROP DATABASE IF EXISTS {TestDb} WITH (FORCE)");
        }
        catch { /* best effort 清理 */ }
    }

    /// <summary>
    /// 断言夹具已就绪。<b>"库连不上"由 <see cref="PostgresFactAttribute"/> 在发现期跳过</b>，
    /// 走到这里说明库是通的、却仍然初始化失败（建库被拒、迁移脚本报错等）—— 那是真故障，必须红，
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
