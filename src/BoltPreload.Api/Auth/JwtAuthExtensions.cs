using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Identity;
using Microsoft.IdentityModel.Tokens;

namespace BoltPreload.Api.Auth;

public sealed class JwtOptions
{
    public string Issuer { get; set; } = "BoltPreload";
    public string Audience { get; set; } = "BoltPreload.Api";
    public string SigningKey { get; set; } = "";
    public int AccessTokenLifetimeMinutes { get; set; } = 480;
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

/// <summary>POCO，单独建一个 user 类纯粹给 PasswordHasher&lt;T&gt; 当泛型参数。</summary>
public sealed class UserAccount
{
    public long Id { get; init; }
    public string Username { get; init; } = "";
    public string Role { get; init; } = AuthRoles.Operator;
}
