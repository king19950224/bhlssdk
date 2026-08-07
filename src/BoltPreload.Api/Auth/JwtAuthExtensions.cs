using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using Npgsql;

namespace BoltPreload.Api.Auth;

/// <summary>JWT 签发与校验配置（appsettings 的 Jwt 节）。生产环境 SigningKey 必填。</summary>
public sealed class JwtOptions
{
    public string Issuer { get; set; } = "BoltPreload";
    public string Audience { get; set; } = "BoltPreload.Api";
    public string SigningKey { get; set; } = "";
    public int AccessTokenLifetimeMinutes { get; set; } = 120;
}

/// <summary>
/// JWT bearer 鉴权配置 + 授权策略。Program.cs 一次性 AddBoltPreloadAuth() 完成所有注册。
/// </summary>
public static class JwtAuthExtensions
{
    public static IServiceCollection AddBoltPreloadAuth(this IServiceCollection services, IConfiguration cfg, bool isDevelopment)
    {
        services.Configure<JwtOptions>(cfg.GetSection("Jwt"));
        var jwt = cfg.GetSection("Jwt").Get<JwtOptions>() ?? new JwtOptions();

        // 启动期强校验：生产没设 SigningKey 直接拒绝起服务。开发可以临时无 key（仅本地）。
        if (string.IsNullOrWhiteSpace(jwt.SigningKey))
        {
            if (!isDevelopment)
                throw new InvalidOperationException(
                    "Jwt:SigningKey 未配置（生产必须）。请通过环境变量 Jwt__SigningKey 或 dotnet user-secrets 注入。");
            jwt.SigningKey = "dev_only_dev_only_dev_only_dev_only_dev_only_dev_only_dev_only_64bytes";
        }
        if (Encoding.UTF8.GetByteCount(jwt.SigningKey) < 32)
            throw new InvalidOperationException("Jwt:SigningKey 至少 32 字节（建议 64）。");

        // 关键：把解析后的有效签名密钥写回 IOptions<JwtOptions>。
        // JwtIssuer 读 IOptions 来签名——若只改了上面的局部变量（dev 兜底/校验用），
        // IOptions 仍是配置里的空 key，导致签发时抛 IDX10703（SymmetricSecurityKey key length is zero）。
        var effectiveSigningKey = jwt.SigningKey;
        services.PostConfigure<JwtOptions>(o => o.SigningKey = effectiveSigningKey);

        services.AddSingleton<IPasswordHasher<UserAccount>, PasswordHasher<UserAccount>>();
        services.AddSingleton<IJwtIssuer, JwtIssuer>();

        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(opts =>
            {
                opts.RequireHttpsMetadata = !isDevelopment;
                opts.SaveToken = false;
                opts.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidateAudience = true,
                    ValidateLifetime = true,
                    ValidateIssuerSigningKey = true,
                    ClockSkew = TimeSpan.FromMinutes(2),
                    ValidIssuer = jwt.Issuer,
                    ValidAudience = jwt.Audience,
                    IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwt.SigningKey))
                };

                // H2：令牌通过签名 / 有效期校验后，再比对 users.token_version 与 enabled。
                // 停用 / 改角色 / 改密码 / 重置 会自增 token_version，使旧令牌立即失效（赋予服务端吊销能力）。
                // 代价：每个携带 JWT 的请求多一次按主键查询；监测系统 QPS 不高可接受，必要时可加短 TTL 缓存。
                opts.Events = new JwtBearerEvents
                {
                    OnTokenValidated = async ctx =>
                    {
                        var principal = ctx.Principal;
                        var idStr = principal?.FindFirstValue(ClaimTypes.NameIdentifier);
                        var verStr = principal?.FindFirstValue(BoltPreloadClaims.TokenVersion);
                        if (!long.TryParse(idStr, out var uid) || !int.TryParse(verStr, out var tokenVer))
                        {
                            ctx.Fail("令牌缺少必要声明");
                            return;
                        }

                        var ds = ctx.HttpContext.RequestServices.GetRequiredService<NpgsqlDataSource>();
                        await using var cmd = ds.CreateCommand(
                            "SELECT token_version, enabled FROM users WHERE id = @id AND deleted_at IS NULL");
                        cmd.Parameters.AddWithValue("id", uid);
                        await using var reader = await cmd.ExecuteReaderAsync(ctx.HttpContext.RequestAborted);
                        if (!await reader.ReadAsync(ctx.HttpContext.RequestAborted))
                        {
                            ctx.Fail("用户不存在或已删除");
                            return;
                        }

                        var currentVersion = reader.GetInt32(0);
                        var enabled = reader.GetBoolean(1);
                        if (!enabled)
                            ctx.Fail("账号已停用");
                        else if (currentVersion != tokenVer)
                            ctx.Fail("令牌已失效（版本不匹配：改密码 / 改角色 / 重新登录后旧令牌作废）");
                    }
                };
            });

        services.AddAuthorization(o =>
        {
            o.AddPolicy(AuthPolicies.LoggedIn,
                p => p.RequireAuthenticatedUser());
            o.AddPolicy(AuthPolicies.WriteAccess,
                p => p.RequireAuthenticatedUser()
                      .RequireRole(AuthRoles.Operator, AuthRoles.Reviewer, AuthRoles.Admin));
            o.AddPolicy(AuthPolicies.ReviewerAccess,
                p => p.RequireAuthenticatedUser()
                      .RequireRole(AuthRoles.Reviewer, AuthRoles.Admin));
            o.AddPolicy(AuthPolicies.AdminAccess,
                p => p.RequireAuthenticatedUser()
                      .RequireRole(AuthRoles.Admin));
        });

        return services;
    }
}
