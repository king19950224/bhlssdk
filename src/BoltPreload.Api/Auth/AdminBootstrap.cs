using Microsoft.AspNetCore.Identity;
using Npgsql;
using NpgsqlTypes;

namespace BoltPreload.Api.Auth;

/// <summary>
/// 启动时检查 users 表里有没有可用的 admin，没有就按环境决定怎么补。
/// 开发模式：直接建 admin / admin123，本地起了服务就能登。
/// 生产模式：不会凭空造账号。先告警，然后只有在配了
/// <c>BoltPreload__BootstrapAdminPassword</c> 时才用这个口令建一次首装管理员；
/// 没配就什么都不做，等运维走 POST /api/auth/users 建。
/// 首装口令的来源与轮换见部署手册 §5。
/// </summary>
public static class AdminBootstrap
{
    /// <summary>
    /// 确保有可用 admin。开发与生产两条路都走这里，
    /// 由 <paramref name="isDevelopment"/> 决定是直接建缺省账号还是只认首装口令。
    /// </summary>
    public static async Task EnsureAdminAsync(IServiceProvider services, IConfiguration cfg, bool isDevelopment, ILogger logger, CancellationToken ct)
    {
        var ds = services.GetRequiredService<NpgsqlDataSource>();
        var hasher = services.GetRequiredService<IPasswordHasher<UserAccount>>();

        await using (var check = ds.CreateCommand(
            "SELECT COUNT(*) FROM users WHERE role = 'admin' AND enabled = true"))
        {
            var n = (long?)await check.ExecuteScalarAsync(ct) ?? 0;
            if (n > 0) return;
        }

        if (!isDevelopment)
        {
            logger.LogWarning(
                "没有可用 admin 账号。请运维通过环境变量 BoltPreload__BootstrapAdminPassword 或 POST /api/auth/users 创建。");
            // 生产环境支持通过环境变量一次性 bootstrap：
            var bootstrapPwd = cfg["BoltPreload:BootstrapAdminPassword"];
            if (string.IsNullOrWhiteSpace(bootstrapPwd)) return;
            await UpsertAdminAsync(ds, hasher, "admin", bootstrapPwd, "Bootstrap Admin", logger, ct);
            return;
        }

        const string defaultDevPwd = "admin123";
        await UpsertAdminAsync(ds, hasher, "admin", defaultDevPwd, "Dev Admin", logger, ct);
        logger.LogWarning("开发模式自动创建 admin / {Pwd}（仅 Development 环境）", defaultDevPwd);
    }

    private static async Task UpsertAdminAsync(NpgsqlDataSource ds, IPasswordHasher<UserAccount> hasher,
        string username, string password, string fullName, ILogger logger, CancellationToken ct)
    {
        var acc = new UserAccount { Username = username, Role = AuthRoles.Admin };
        var hash = hasher.HashPassword(acc, password);

        const string sql = @"
            INSERT INTO users (username, full_name, email, password_hash, role, enabled)
            VALUES (@u, @full, NULL, @h, 'admin', true)
            ON CONFLICT (username) DO UPDATE
                SET password_hash = EXCLUDED.password_hash,
                    role          = 'admin',
                    enabled       = true,
                    deleted_at    = NULL,
                    failed_attempts = 0,
                    locked_until = NULL,
                    token_version = users.token_version + 1";
        await using var cmd = ds.CreateCommand(sql);
        cmd.Parameters.AddWithValue("u", NpgsqlDbType.Varchar, username);
        cmd.Parameters.AddWithValue("full", NpgsqlDbType.Varchar, fullName);
        cmd.Parameters.AddWithValue("h", NpgsqlDbType.Varchar, hash);
        await cmd.ExecuteNonQueryAsync(ct);
        logger.LogInformation("Bootstrap admin 完成 username={U}", username);
    }
}
