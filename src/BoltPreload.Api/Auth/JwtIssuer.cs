using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace BoltPreload.Api.Auth;

public interface IJwtIssuer
{
    string Issue(long userId, string username, string role, int tokenVersion);
}

/// <summary>自定义 JWT claim 名。</summary>
public static class BoltPreloadClaims
{
    /// <summary>令牌签发时的用户版本号（H2）。鉴权时与 users.token_version 比对，不一致即视为已吊销。</summary>
    public const string TokenVersion = "token_version";
}

public sealed class JwtIssuer : IJwtIssuer
{
    private readonly JwtOptions _opt;

    public JwtIssuer(IOptions<JwtOptions> opt)
    {
        _opt = opt.Value;
    }

    public string Issue(long userId, string username, string role, int tokenVersion)
    {
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_opt.SigningKey));
        var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

        var token = new JwtSecurityToken(
            issuer: _opt.Issuer,
            audience: _opt.Audience,
            claims: new[]
            {
                new Claim(JwtRegisteredClaimNames.Sub, userId.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                new Claim(ClaimTypes.NameIdentifier, userId.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                new Claim(ClaimTypes.Name, username),
                new Claim(ClaimTypes.Role, role),
                // H2：内嵌签发时的用户版本号，鉴权时比对 users.token_version，停用/改角色/改密码后旧令牌立即失效
                new Claim(BoltPreloadClaims.TokenVersion, tokenVersion.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString("N"))
            },
            notBefore: DateTime.UtcNow,
            expires: DateTime.UtcNow.AddMinutes(_opt.AccessTokenLifetimeMinutes),
            signingCredentials: creds);

        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}
