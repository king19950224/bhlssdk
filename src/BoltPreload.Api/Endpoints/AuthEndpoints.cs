using BoltPreload.Api.Auth;
using Microsoft.AspNetCore.Identity;
using Npgsql;
using NpgsqlTypes;

namespace BoltPreload.Api.Endpoints;

/// <summary>
/// 认证与账号管理端点（/api/auth）：登录换 JWT、登出、当前用户信息、改密，
/// 以及管理员建用户/停启用。登录失败锁定与 token_version 失效策略见各端点内注释。
/// </summary>
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
                FROM users WHERE username = @u AND deleted_at IS NULL";
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
                // 与"用户不存在"分支同延时同状态码：否则响应时序差异可区分"存在但被禁用"，辅助账号枚举
                await Task.Delay(120, ct);
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
                await Task.Delay(120, ct);  // 同上：拉平时序差异
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

        // 登出：服务端吊销。仅前端清 localStorage 挡不住已泄露/被窥的令牌继续用到过期(默认 8h)；
        // token_version 自增让该用户全部已签发令牌立即失效(共享工控机/离岗场景的硬性要求)。
        grp.MapPost("/logout", async (HttpContext http, NpgsqlDataSource ds, CancellationToken ct) =>
        {
            var user = UserContext.Require(http);
            await using var cmd = ds.CreateCommand(
                "UPDATE users SET token_version = token_version + 1 WHERE id = @id AND deleted_at IS NULL");
            cmd.Parameters.AddWithValue("id", NpgsqlDbType.Bigint, user.UserId);
            await cmd.ExecuteNonQueryAsync(ct);
            return Results.Ok(new { loggedOut = true });
        })
        .RequireAuthorization(AuthPolicies.LoggedIn)
        .WithName("Logout")
        .WithSummary("登出（令牌版本自增,该用户所有已签发令牌立即失效）");

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

            await using var conn = await ds.OpenConnectionAsync(ct);
            await using var tx = await conn.BeginTransactionAsync(ct);

            // 本端点按 username upsert，会直接覆盖既有用户的 role——
            // 若目标是现存 enabled admin 且要降为非 admin，必须保证还有别的 enabled admin，
            // 否则唯一管理员一次"重置自己"就把系统锁死在无人可管的状态。
            // FOR UPDATE 锁住全部 enabled admin 行，防两个并发降权请求互相看不见对方。
            if (req.Role != AuthRoles.Admin)
            {
                var targetIsEnabledAdmin = false;
                var otherEnabledAdmins = 0;
                await using (var cmd = new NpgsqlCommand(@"
                    SELECT username FROM users
                    WHERE role = @admin AND enabled = true AND deleted_at IS NULL
                    FOR UPDATE", conn, tx))
                {
                    cmd.Parameters.AddWithValue("admin", NpgsqlDbType.Varchar, AuthRoles.Admin);
                    await using var r = await cmd.ExecuteReaderAsync(ct);
                    while (await r.ReadAsync(ct))
                    {
                        if (string.Equals(r.GetString(0), req.Username, StringComparison.Ordinal))
                            targetIsEnabledAdmin = true;
                        else
                            otherEnabledAdmins++;
                    }
                }
                if (targetIsEnabledAdmin && otherEnabledAdmins == 0)
                {
                    await tx.RollbackAsync(ct);
                    return Results.BadRequest(new { error = "不能把最后一名启用状态的管理员降权：系统将失去用户管理能力" });
                }
            }

            const string sql = @"
                INSERT INTO users (username, full_name, email, password_hash, role, enabled)
                VALUES (@u, @full, @em, @h, @r, true)
                ON CONFLICT (username) DO UPDATE
                    SET full_name = COALESCE(EXCLUDED.full_name, users.full_name),
                        email     = COALESCE(EXCLUDED.email, users.email),
                        password_hash = EXCLUDED.password_hash,
                        role      = EXCLUDED.role,
                        enabled   = true,
                        deleted_at = NULL,
                        failed_attempts = 0,
                        locked_until = NULL,
                        token_version = users.token_version + 1
                RETURNING id";
            long id;
            await using (var cmd = new NpgsqlCommand(sql, conn, tx))
            {
                cmd.Parameters.AddWithValue("u", NpgsqlDbType.Varchar, req.Username);
                cmd.Parameters.AddWithValue("full", NpgsqlDbType.Varchar, (object?)req.FullName ?? DBNull.Value);
                cmd.Parameters.AddWithValue("em", NpgsqlDbType.Varchar, (object?)req.Email ?? DBNull.Value);
                cmd.Parameters.AddWithValue("h", NpgsqlDbType.Varchar, hash);
                cmd.Parameters.AddWithValue("r", NpgsqlDbType.Varchar, req.Role);
                id = (long)(await cmd.ExecuteScalarAsync(ct) ?? 0L);
            }
            await tx.CommitAsync(ct);

            var actor = UserContext.Require(http);
            await WriteUserAuditAsync(ds, actor.UserId, "user_create", id,
                $"创建/重置用户 {req.Username}（角色 {req.Role}）", ct);

            return Results.Ok(new { id, username = req.Username, role = req.Role });
        })
        .RequireAuthorization(AuthPolicies.AdminAccess)
        .WithName("CreateUser")
        .WithSummary("创建 / 重置用户（admin；不可把最后一名管理员降权）");

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

        // 启用 / 停用用户（admin,不能停用自己,不能停用最后一名 enabled admin）
        grp.MapPost("/users/{id:long}/toggle", async (long id, HttpContext http, NpgsqlDataSource ds, CancellationToken ct) =>
        {
            var actor = UserContext.Require(http);
            if (actor.UserId == id) return Results.BadRequest(new { error = "不能停用自己的账号" });

            await using var conn = await ds.OpenConnectionAsync(ct);
            await using var tx = await conn.BeginTransactionAsync(ct);

            // 先锁目标行读当前态（reader 关闭后再回滚/返回，避免 404 变 500）
            string? username = null;
            string role = "";
            var wasEnabled = false;
            await using (var cmd = new NpgsqlCommand(@"
                SELECT username, role, enabled FROM users
                WHERE id = @id AND deleted_at IS NULL
                FOR UPDATE", conn, tx))
            {
                cmd.Parameters.AddWithValue("id", NpgsqlDbType.Bigint, id);
                await using var r = await cmd.ExecuteReaderAsync(ct);
                if (await r.ReadAsync(ct))
                {
                    username = r.GetString(0);
                    role = r.GetString(1);
                    wasEnabled = r.GetBoolean(2);
                }
            }
            if (username is null)
            {
                await tx.RollbackAsync(ct);
                return Results.NotFound(new { error = "用户不存在", id });
            }

            // 即将停用一名 enabled admin：必须还有别的 enabled admin，否则系统失去用户管理能力
            if (wasEnabled && role == AuthRoles.Admin)
            {
                await using var cnt = new NpgsqlCommand(@"
                    SELECT count(*) FROM users
                    WHERE role = @admin AND enabled = true AND deleted_at IS NULL AND id <> @id", conn, tx);
                cnt.Parameters.AddWithValue("admin", NpgsqlDbType.Varchar, AuthRoles.Admin);
                cnt.Parameters.AddWithValue("id", NpgsqlDbType.Bigint, id);
                if ((long)(await cnt.ExecuteScalarAsync(ct))! == 0)
                {
                    await tx.RollbackAsync(ct);
                    return Results.BadRequest(new { error = "不能停用最后一名启用状态的管理员" });
                }
            }

            bool enabled;
            await using (var cmd = new NpgsqlCommand(@"
                UPDATE users SET enabled = NOT enabled, token_version = token_version + 1 WHERE id = @id
                RETURNING enabled", conn, tx))
            {
                cmd.Parameters.AddWithValue("id", NpgsqlDbType.Bigint, id);
                enabled = (bool)(await cmd.ExecuteScalarAsync(ct))!;
            }
            await tx.CommitAsync(ct);

            await WriteUserAuditAsync(ds, actor.UserId, "user_toggle", id, $"{(enabled ? "启用" : "停用")}用户 {username}", ct);
            return Results.Ok(new { id, username, enabled });
        })
        .RequireAuthorization(AuthPolicies.AdminAccess)
        .WithName("ToggleUser")
        .WithSummary("启用/停用用户（admin；保护最后一名管理员）");

        // 自助改密：验证旧密码 → 写新 hash → token_version 自增（本人所有旧令牌失效,须重新登录）。
        // 没有这个端点时用户只能找 admin 重置,现场用不起来。
        grp.MapPost("/change-password", async (
            ChangePasswordRequest req, HttpContext http, NpgsqlDataSource ds,
            IPasswordHasher<UserAccount> hasher, CancellationToken ct) =>
        {
            var actor = UserContext.Require(http);
            if (string.IsNullOrWhiteSpace(req.OldPassword) || string.IsNullOrWhiteSpace(req.NewPassword))
                return Results.BadRequest(new { error = "旧密码与新密码必填" });
            if (req.NewPassword.Length < 8)
                return Results.BadRequest(new { error = "新密码至少 8 位" });
            if (req.NewPassword == req.OldPassword)
                return Results.BadRequest(new { error = "新密码不能与旧密码相同" });

            string? hash = null, username = null, role = null;
            await using (var cmd = ds.CreateCommand(
                "SELECT password_hash, username, role FROM users WHERE id = @id AND enabled = true AND deleted_at IS NULL"))
            {
                cmd.Parameters.AddWithValue("id", NpgsqlDbType.Bigint, actor.UserId);
                await using var r = await cmd.ExecuteReaderAsync(ct);
                if (await r.ReadAsync(ct))
                {
                    hash = r.IsDBNull(0) ? null : r.GetString(0);
                    username = r.GetString(1);
                    role = r.GetString(2);
                }
            }
            if (hash is null || username is null)
                return Results.Unauthorized();

            var account = new UserAccount { Id = actor.UserId, Username = username, Role = role ?? AuthRoles.Operator };
            if (hasher.VerifyHashedPassword(account, hash, req.OldPassword) == PasswordVerificationResult.Failed)
                return Results.BadRequest(new { error = "旧密码不正确" });

            var newHash = hasher.HashPassword(account, req.NewPassword);
            await using (var upd = ds.CreateCommand(@"
                UPDATE users SET password_hash = @h, token_version = token_version + 1 WHERE id = @id"))
            {
                upd.Parameters.AddWithValue("h", NpgsqlDbType.Varchar, newHash);
                upd.Parameters.AddWithValue("id", NpgsqlDbType.Bigint, actor.UserId);
                await upd.ExecuteNonQueryAsync(ct);
            }

            await WriteUserAuditAsync(ds, actor.UserId, "user_change_password", actor.UserId, $"用户 {username} 自助修改密码", ct);
            return Results.Ok(new { changed = true, note = "密码已修改,请用新密码重新登录" });
        })
        .RequireAuthorization(AuthPolicies.LoggedIn)
        .WithName("ChangePassword")
        .WithSummary("自助修改密码（验证旧密码;改后所有旧令牌失效须重新登录）");

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

/// <summary>登录。连续输错会锁账号，锁定判定按自增后的真实失败数来，防的是并发撞库。</summary>
public sealed record LoginRequest(string Username, string Password);

/// <summary>登录成功返回：JWT 加用户基本信息，前端拿到就能渲染顶栏，不用再问一次。</summary>
public sealed record LoginResponse(string AccessToken, long UserId, string Username, string Role);

/// <summary>
/// 管理员建号。注意是按 username upsert：同名会把密码和角色一起覆盖掉，
/// 所以这个接口同时也是“重置用户”的入口，改别人角色走的也是它。
/// </summary>
public sealed record CreateUserRequest(string Username, string Password, string Role, string? FullName = null, string? Email = null);

/// <summary>改自己的密码，得先验旧密码。改完 token_version 会跳，手上的旧令牌立刻作废。</summary>
public sealed record ChangePasswordRequest(string OldPassword, string NewPassword);
