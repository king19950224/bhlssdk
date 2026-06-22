using BoltPreload.Api.Auth;
using Microsoft.AspNetCore.Identity;
using Npgsql;
using NpgsqlTypes;

namespace BoltPreload.Api.Endpoints;

public static class AuthEndpoints
{
    public static IEndpointRouteBuilder MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        var grp = app.MapGroup("/api/auth").WithTags("Auth");

        // 登录：用户名 + 密码 → JWT。匿名可访问。
        grp.MapPost("/login", async (
            LoginRequest req, NpgsqlDataSource ds,
            IPasswordHasher<UserAccount> hasher, IJwtIssuer issuer,
            ILoggerFactory loggerFactory, CancellationToken ct) =>
        {
            var logger = loggerFactory.CreateLogger("Auth.Login");
            if (string.IsNullOrWhiteSpace(req.Username) || string.IsNullOrWhiteSpace(req.Password))
                return Results.BadRequest(new { error = "username 与 password 必填" });

            const string sql = @"
                SELECT id, username, role, password_hash, enabled, locked_until, failed_attempts, token_version
                FROM users WHERE username = @u";
            await using var cmd = ds.CreateCommand(sql);
            cmd.Parameters.AddWithValue("u", NpgsqlDbType.Varchar, req.Username);
            await using var r = await cmd.ExecuteReaderAsync(ct);

            // 故意延时一小段，让"用户不存在" 和 "密码错"耗时相近，弱化用户枚举
            if (!await r.ReadAsync(ct))
            {
                await Task.Delay(120, ct);
                logger.LogInformation("登录失败 username={U}（用户不存在）", req.Username);
                return Results.Unauthorized();
            }

            var id = r.GetInt64(0);
            var username = r.GetString(1);
            var role = r.GetString(2);
            var hash = r.GetString(3);
            var enabled = r.GetBoolean(4);
            var lockedUntil = r.IsDBNull(5) ? (DateTime?)null : r.GetDateTime(5);
            var tokenVersion = r.GetInt32(7);
            await r.CloseAsync();

            if (!enabled)
            {
                logger.LogWarning("登录失败 username={U}（账号已禁用）", username);
                return Results.Unauthorized();
            }
            if (lockedUntil.HasValue && lockedUntil.Value > DateTime.UtcNow)
            {
                logger.LogWarning("登录被锁 username={U} until={Until}", username, lockedUntil);
                return Results.StatusCode(StatusCodes.Status423Locked);
            }
            if (string.IsNullOrEmpty(hash))
            {
                logger.LogWarning("登录失败 username={U}（无 password_hash，账号待设置）", username);
                return Results.Unauthorized();
            }

            var account = new UserAccount { Id = id, Username = username, Role = role };
            var verifyResult = hasher.VerifyHashedPassword(account, hash, req.Password);
            if (verifyResult == PasswordVerificationResult.Failed)
            {
                var attempts = await BumpFailureAsync(ds, id, ct);
                logger.LogInformation("登录失败 username={U}（密码错误，连续失败={N}）", username, attempts);
                return Results.Unauthorized();
            }

            // SuccessRehashNeeded：用旧迭代次数的 hash 验证通过，顺手用最新迭代重写一次
            if (verifyResult == PasswordVerificationResult.SuccessRehashNeeded)
            {
                var fresh = hasher.HashPassword(account, req.Password);
                await using var upd = ds.CreateCommand(
                    "UPDATE users SET password_hash=@h WHERE id=@id");
                upd.Parameters.AddWithValue("h", NpgsqlDbType.Varchar, fresh);
                upd.Parameters.AddWithValue("id", NpgsqlDbType.Bigint, id);
                await upd.ExecuteNonQueryAsync(ct);
            }

            await using (var clear = ds.CreateCommand(
                "UPDATE users SET failed_attempts = 0, last_login_at = now() WHERE id=@id"))
            {
                clear.Parameters.AddWithValue("id", NpgsqlDbType.Bigint, id);
                await clear.ExecuteNonQueryAsync(ct);
            }

            var token = issuer.Issue(id, username, role, tokenVersion);
            return Results.Ok(new LoginResponse(token, id, username, role));
        })
        .AllowAnonymous()
        .WithName("Login")
        .WithSummary("用户名 + 密码 → JWT");

        // 查当前用户。需登录。
        grp.MapGet("/me", (HttpContext http) =>
        {
            var u = UserContext.TryGet(http);
            return u is null ? Results.Unauthorized() : Results.Ok(u);
        })
        .RequireAuthorization(AuthPolicies.LoggedIn)
        .WithName("CurrentUser")
        .WithSummary("当前登录用户");

        // 管理员创建 / 重置用户。仅 admin。
        grp.MapPost("/users", async (
            CreateUserRequest req, HttpContext http, NpgsqlDataSource ds,
            IPasswordHasher<UserAccount> hasher, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(req.Username) || string.IsNullOrWhiteSpace(req.Password))
                return Results.BadRequest(new { error = "username 与 password 必填" });
            if (req.Password.Length < 8)
                return Results.BadRequest(new { error = "密码至少 8 位" });
            if (!AuthRoles.All.Contains(req.Role))
                return Results.BadRequest(new { error = $"role 必须是 {string.Join('/', AuthRoles.All)}" });

            var acc = new UserAccount { Username = req.Username, Role = req.Role };
            var hash = hasher.HashPassword(acc, req.Password);

            const string sql = @"
                INSERT INTO users (username, full_name, email, password_hash, role, enabled)
                VALUES (@u, @full, @em, @h, @r, true)
                ON CONFLICT (username) DO UPDATE
                    SET full_name = COALESCE(EXCLUDED.full_name, users.full_name),
                        email     = COALESCE(EXCLUDED.email, users.email),
                        password_hash = EXCLUDED.password_hash,
                        role      = EXCLUDED.role,
                        enabled   = true,
                        failed_attempts = 0,
                        locked_until = NULL,
                        token_version = users.token_version + 1
                RETURNING id";
            await using var cmd = ds.CreateCommand(sql);
            cmd.Parameters.AddWithValue("u", NpgsqlDbType.Varchar, req.Username);
            cmd.Parameters.AddWithValue("full", NpgsqlDbType.Varchar, (object?)req.FullName ?? DBNull.Value);
            cmd.Parameters.AddWithValue("em", NpgsqlDbType.Varchar, (object?)req.Email ?? DBNull.Value);
            cmd.Parameters.AddWithValue("h", NpgsqlDbType.Varchar, hash);
            cmd.Parameters.AddWithValue("r", NpgsqlDbType.Varchar, req.Role);
            var id = (long)(await cmd.ExecuteScalarAsync(ct) ?? 0L);

            var actor = UserContext.Require(http);
            await WriteUserAuditAsync(ds, actor.UserId, "user_create", id,
                $"创建/重置用户 {req.Username}（角色 {req.Role}）", ct);

            return Results.Ok(new { id, username = req.Username, role = req.Role });
        })
        .RequireAuthorization(AuthPolicies.AdminAccess)
        .WithName("CreateUser")
        .WithSummary("创建 / 重置用户（admin）");

        // 用户列表（admin）
        grp.MapGet("/users", async (NpgsqlDataSource ds, CancellationToken ct) =>
        {
            await using var cmd = ds.CreateCommand(@"
                SELECT id, username, full_name, role, enabled, last_login_at, created_at, locked_until
                FROM users WHERE deleted_at IS NULL ORDER BY id");
            var items = new List<object>();
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
                items.Add(new
                {
                    id = r.GetInt64(0),
                    username = r.GetString(1),
                    fullName = r.IsDBNull(2) ? null : r.GetString(2),
                    role = r.GetString(3),
                    enabled = r.GetBoolean(4),
                    lastLoginAt = r.IsDBNull(5) ? (DateTime?)null : r.GetDateTime(5),
                    createdAt = r.GetDateTime(6),
                    locked = !r.IsDBNull(7) && r.GetDateTime(7) > DateTime.UtcNow,
                });
            return Results.Ok(new { count = items.Count, items });
        })
        .RequireAuthorization(AuthPolicies.AdminAccess)
        .WithName("ListUsers")
        .WithSummary("用户列表（admin）");

        // 启用 / 停用用户（admin,不能停用自己）
        grp.MapPost("/users/{id:long}/toggle", async (long id, HttpContext http, NpgsqlDataSource ds, CancellationToken ct) =>
        {
            var actor = UserContext.Require(http);
            if (actor.UserId == id) return Results.BadRequest(new { error = "不能停用自己的账号" });

            string username;
            bool enabled;
            await using (var cmd = ds.CreateCommand(@"
                UPDATE users SET enabled = NOT enabled, token_version = token_version + 1 WHERE id=@id AND deleted_at IS NULL
                RETURNING username, enabled"))
            {
                cmd.Parameters.AddWithValue("id", NpgsqlDbType.Bigint, id);
                await using var r = await cmd.ExecuteReaderAsync(ct);
                if (!await r.ReadAsync(ct)) return Results.NotFound(new { error = "用户不存在", id });
                username = r.GetString(0);
                enabled = r.GetBoolean(1);
            }
            await WriteUserAuditAsync(ds, actor.UserId, "user_toggle", id, $"{(enabled ? "启用" : "停用")}用户 {username}", ct);
            return Results.Ok(new { id, username, enabled });
        })
        .RequireAuthorization(AuthPolicies.AdminAccess)
        .WithName("ToggleUser")
        .WithSummary("启用/停用用户（admin）");

        return app;
    }

    private static async Task WriteUserAuditAsync(NpgsqlDataSource ds, long actorId, string action, long targetUserId, string note, CancellationToken ct)
    {
        await using var cmd = ds.CreateCommand(@"
            INSERT INTO audit_log (user_id, action, target_type, target_id, before_jsonb, after_jsonb, note, at)
            VALUES (@uid, @action, 'user', @tid, NULL, NULL, @note, now())");
        cmd.Parameters.AddWithValue("uid", NpgsqlDbType.Bigint, actorId);
        cmd.Parameters.AddWithValue("action", NpgsqlDbType.Varchar, action);
        cmd.Parameters.AddWithValue("tid", NpgsqlDbType.Bigint, targetUserId);
        cmd.Parameters.AddWithValue("note", NpgsqlDbType.Text, note);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    // 登录失败计数：必须在 DB 内原子自增（failed_attempts = failed_attempts + 1），
    // 不能用"登录时读到的值 + 1"回写——并发失败请求会 lost update，使计数停滞、
    // 锁定阈值被绕过、可无限撞库（H1）。锁定判定同样基于自增后的真实值。
    // 返回自增后的真实失败次数（供日志）。
    private static async Task<int> BumpFailureAsync(NpgsqlDataSource ds, long userId, CancellationToken ct)
    {
        const int lockThreshold = 5;   // 失败 5 次以上锁 15 分钟（合理保护，仍可由 admin 解锁）
        const int lockMinutes = 15;
        const string sql = @"
            UPDATE users
               SET failed_attempts = failed_attempts + 1,
                   locked_until = CASE
                       WHEN failed_attempts + 1 >= @threshold
                       THEN now() + (@lockMinutes * interval '1 minute')
                       ELSE locked_until
                   END
             WHERE id = @id
            RETURNING failed_attempts";
        await using var cmd = ds.CreateCommand(sql);
        cmd.Parameters.AddWithValue("threshold", NpgsqlDbType.Integer, lockThreshold);
        cmd.Parameters.AddWithValue("lockMinutes", NpgsqlDbType.Integer, lockMinutes);
        cmd.Parameters.AddWithValue("id", NpgsqlDbType.Bigint, userId);
        var result = await cmd.ExecuteScalarAsync(ct);
        return result switch { short s => s, int i => i, long l => (int)l, _ => 0 };
    }
}

public sealed record LoginRequest(string Username, string Password);
public sealed record LoginResponse(string AccessToken, long UserId, string Username, string Role);
public sealed record CreateUserRequest(string Username, string Password, string Role, string? FullName = null, string? Email = null);
