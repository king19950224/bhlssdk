using Microsoft.AspNetCore.Identity;
using Npgsql;
using NpgsqlTypes;

namespace BoltPreload.Api.Auth;

/// <summary>
/// 启动时检查 users 表是否有 enabled admin。
/// 开发模式：无 admin 时自动创建 admin / admin123，方便本地起服务即可登录。
/// 生产模式：无 admin 时只 LogWarning，等运维通过 dotnet user-secrets 或 POST /api/auth/users 注入。
/// </summary>
public static class AdminBootstrap
{
    public static async Task EnsureDevAdminAsync(IServiceProvider services, IConfiguration cfg, bool isDevelopment, ILogger logger, CancellationToken ct)
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
