using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using BoltPreload.Api.Auth;
using FluentAssertions;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using NpgsqlTypes;
using Xunit;

namespace BoltPreload.Tests.Integration;

/// <summary>
/// 真实 JWT 鉴权链：登录换令牌、签名与有效期校验、以及吊销复核（<c>OnTokenValidated</c> 里
/// 按 users.token_version 与 enabled 二次比对）。
/// <para>
/// 这条链此前<b>整条零覆盖</b>。其余端点测试都跑在 <c>TestAuthHandler</c> 上，按请求头认身份，
/// 根本不经过 JWT。于是安全红线"停用 / 改密 / 登出后旧令牌立即失效"从来没被验证过：
/// 已有测试只断言了 users.token_version 在库里自增，而自增之后那个旧令牌还能不能用——
/// 也就是这套机制唯一存在的理由——没有任何测试问过。把 <c>OnTokenValidated</c> 整段删掉，
/// 351 个测试照样全绿，而被停用的账号还能拿旧令牌写数据，最长到令牌自然过期（默认 8 小时）。
/// </para>
/// <para>
/// 令牌默认 8 小时有效，共享工控机、人员离岗、账号被撤这些场景全靠这条复核收口，
/// 所以这里逐条把它钉住。
/// </para>
/// </summary>
[Collection("postgres")]
public sealed class RealJwtAuthIntegrationTests
{
    private const string GoodPassword = "Correct-Horse-9";

    private readonly PostgresFixture _fx;
    public RealJwtAuthIntegrationTests(PostgresFixture fx) => _fx = fx;

    [PostgresFact]
    public async Task Login_WithCorrectPassword_IssuesTokenThatOpensProtectedRoutes()
    {
        _fx.EnsureAvailable();
        using var factory = TestApi.CreateRealAuthFactory(_fx);
        var (_, username) = await SeedLoginableUserAsync("operator");

        var token = await LoginAsync(factory, username, GoodPassword);
        token.Should().NotBeNullOrWhiteSpace();

        // 这条先立住：下面几条全是断言"401"，端点整个坏掉时它们也会绿。
        using var client = Bearer(factory, token!);
        var me = await client.GetAsync("/api/auth/me");
        me.StatusCode.Should().Be(HttpStatusCode.OK);
        (await me.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("username").GetString()
            .Should().Be(username);
    }

    [PostgresFact]
    public async Task DisablingUser_KillsAlreadyIssuedToken_Immediately()
    {
        _fx.EnsureAvailable();
        using var factory = TestApi.CreateRealAuthFactory(_fx);
        var (victimId, victimName) = await SeedLoginableUserAsync("operator");
        var (_, adminName) = await SeedLoginableUserAsync("admin");

        var victimToken = await LoginAsync(factory, victimName, GoodPassword);
        using var victim = Bearer(factory, victimToken!);
        (await victim.GetAsync("/api/auth/me")).StatusCode.Should().Be(HttpStatusCode.OK);

        // 管理员走真实端点停用该账号
        using var admin = Bearer(factory, (await LoginAsync(factory, adminName, GoodPassword))!);
        (await admin.PostAsync($"/api/auth/users/{victimId}/toggle", null))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        // 要害：令牌本身仍然签名有效、也没过期，必须靠这次复核当场拒掉。
        // 这一步失败意味着被撤权的人还能继续写数据，直到令牌自然过期。
        (await victim.GetAsync("/api/auth/me")).StatusCode.Should().Be(HttpStatusCode.Unauthorized,
            "停用后旧令牌必须立即失效，而不是等它 8 小时后过期");
    }

    [PostgresFact]
    public async Task ChangingOwnPassword_KillsPreviouslyIssuedTokens()
    {
        _fx.EnsureAvailable();
        using var factory = TestApi.CreateRealAuthFactory(_fx);
        var (_, username) = await SeedLoginableUserAsync("operator");

        // 两个令牌模拟两台机器上的两个会话
        var sessionA = await LoginAsync(factory, username, GoodPassword);
        var sessionB = await LoginAsync(factory, username, GoodPassword);
        using var a = Bearer(factory, sessionA!);
        using var b = Bearer(factory, sessionB!);

        var changed = await a.PostAsJsonAsync("/api/auth/change-password",
            new { oldPassword = GoodPassword, newPassword = "Another-Horse-77" });
        changed.StatusCode.Should().Be(HttpStatusCode.OK);

        // 改密的初衷是"密码可能已泄露"，所以另一台机器上那个会话也必须一起断，
        // 只断当前这个等于没防住。
        (await a.GetAsync("/api/auth/me")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await b.GetAsync("/api/auth/me")).StatusCode.Should().Be(HttpStatusCode.Unauthorized,
            "改密要让该用户所有已签发令牌失效，不只是发起改密的那一个");

        // 新密码能登进来，旧密码不行
        (await LoginAsync(factory, username, "Another-Horse-77")).Should().NotBeNull();
        (await LoginAsync(factory, username, GoodPassword)).Should().BeNull();
    }

    [PostgresFact]
    public async Task Logout_KillsTokenServerSide_NotJustInTheBrowser()
    {
        _fx.EnsureAvailable();
        using var factory = TestApi.CreateRealAuthFactory(_fx);
        var (_, username) = await SeedLoginableUserAsync("operator");

        using var client = Bearer(factory, (await LoginAsync(factory, username, GoodPassword))!);
        (await client.PostAsync("/api/auth/logout", null)).StatusCode.Should().Be(HttpStatusCode.OK);

        // 前端清 localStorage 挡不住被人抄走的令牌，服务端必须真的作废它
        (await client.GetAsync("/api/auth/me")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [PostgresFact]
    public async Task Login_LocksAccountAfterFiveWrongPasswords()
    {
        _fx.EnsureAvailable();
        using var factory = TestApi.CreateRealAuthFactory(_fx);
        var (userId, username) = await SeedLoginableUserAsync("operator");
        using var anonymous = factory.CreateClient();

        for (var i = 1; i <= 4; i++)
        {
            var attempt = await anonymous.PostAsJsonAsync("/api/auth/login",
                new { username, password = "wrong-" + i });
            attempt.StatusCode.Should().Be(HttpStatusCode.Unauthorized, $"第 {i} 次错密码还不该锁");
        }

        // 第 5 次达到阈值：锁定 15 分钟，状态码换成 423，前端才能区分"密码错"和"已锁定"
        var fifth = await anonymous.PostAsJsonAsync("/api/auth/login", new { username, password = "wrong-5" });
        fifth.StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        var locked = await anonymous.PostAsJsonAsync("/api/auth/login", new { username, password = GoodPassword });
        locked.StatusCode.Should().Be(HttpStatusCode.Locked,
            "连错 5 次后必须锁住，哪怕这次密码是对的——否则撞库不受限");

        (await _fx.ScalarAsync<bool>(
            "SELECT locked_until IS NOT NULL AND locked_until > now() FROM users WHERE id=@arg", userId))
            .Should().BeTrue();
        // failed_attempts 是 smallint
        (await _fx.ScalarAsync<short>("SELECT failed_attempts FROM users WHERE id=@arg", userId))
            .Should().BeGreaterOrEqualTo(5, "失败计数要在库里原子自增，不能被并发请求覆盖掉");
    }

    [PostgresFact]
    public async Task TamperedAndForeignTokens_AreRejected()
    {
        _fx.EnsureAvailable();
        using var factory = TestApi.CreateRealAuthFactory(_fx);
        var (_, username) = await SeedLoginableUserAsync("operator");
        var token = (await LoginAsync(factory, username, GoodPassword))!;

        // 改签名段：能过就等于任何人都能自己造令牌
        var parts = token.Split('.');
        parts.Should().HaveCount(3);
        var tamperedSignature = string.Join('.', parts[0], parts[1], Flip(parts[2]));
        using (var c = Bearer(factory, tamperedSignature))
            (await c.GetAsync("/api/auth/me")).StatusCode.Should().Be(HttpStatusCode.Unauthorized, "签名被改必须拒");

        // 改载荷段（比如把 role 提到 admin）会让签名对不上，同样必须拒
        var tamperedPayload = string.Join('.', parts[0], Flip(parts[1]), parts[2]);
        using (var c = Bearer(factory, tamperedPayload))
            (await c.GetAsync("/api/auth/me")).StatusCode.Should().Be(HttpStatusCode.Unauthorized, "载荷被改必须拒");

        using (var c = Bearer(factory, "not-even-a-jwt"))
            (await c.GetAsync("/api/auth/me")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [PostgresFact]
    public async Task TokenOfSoftDeletedUser_IsRejected()
    {
        _fx.EnsureAvailable();
        using var factory = TestApi.CreateRealAuthFactory(_fx);
        var (userId, username) = await SeedLoginableUserAsync("operator");
        using var client = Bearer(factory, (await LoginAsync(factory, username, GoodPassword))!);
        (await client.GetAsync("/api/auth/me")).StatusCode.Should().Be(HttpStatusCode.OK);

        await using (var cmd = _fx.DataSource.CreateCommand("UPDATE users SET deleted_at = now() WHERE id = @id"))
        {
            cmd.Parameters.AddWithValue("id", NpgsqlDbType.Bigint, userId);
            await cmd.ExecuteNonQueryAsync();
        }

        // 软删除的账号 token_version 没变，只有那句 deleted_at IS NULL 拦得住它
        (await client.GetAsync("/api/auth/me")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [PostgresFact]
    public async Task RoleInToken_IsWhatAuthorizationActuallyUses()
    {
        _fx.EnsureAvailable();
        using var factory = TestApi.CreateRealAuthFactory(_fx);
        var (_, operatorName) = await SeedLoginableUserAsync("operator");
        var (_, adminName) = await SeedLoginableUserAsync("admin");

        // 角色声明是从库里读出来签进令牌的，鉴权按它判。这条确认真实链路上
        // 角色确实生效——而不是只在 TestAuthHandler 造的请求头里生效。
        using var op = Bearer(factory, (await LoginAsync(factory, operatorName, GoodPassword))!);
        (await op.GetAsync("/api/auth/users")).StatusCode.Should().Be(HttpStatusCode.Forbidden);

        using var admin = Bearer(factory, (await LoginAsync(factory, adminName, GoodPassword))!);
        (await admin.GetAsync("/api/auth/users")).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // ===================== 铺数据与请求 =====================

    /// <summary>建一个能真正登录的账号：password_hash 用生产同一个 hasher 算，不是占位串。</summary>
    private async Task<(long Id, string Username)> SeedLoginableUserAsync(string role)
    {
        var username = "u_" + Guid.NewGuid().ToString("N")[..12];
        var hash = new PasswordHasher<UserAccount>().HashPassword(
            new UserAccount { Username = username, Role = role }, GoodPassword);

        await using var cmd = _fx.DataSource.CreateCommand(
            "INSERT INTO users (username, password_hash, role, enabled) VALUES (@u, @h, @r, true) RETURNING id");
        cmd.Parameters.AddWithValue("u", NpgsqlDbType.Varchar, username);
        cmd.Parameters.AddWithValue("h", NpgsqlDbType.Varchar, hash);
        cmd.Parameters.AddWithValue("r", NpgsqlDbType.Varchar, role);
        return ((long)(await cmd.ExecuteScalarAsync())!, username);
    }

    /// <summary>登录并取出 accessToken；凭据不通过时返回 null。</summary>
    private static async Task<string?> LoginAsync(
        WebApplicationFactory<Program> factory, string username, string password)
    {
        using var client = factory.CreateClient();
        var resp = await client.PostAsJsonAsync("/api/auth/login", new { username, password });
        if (resp.StatusCode != HttpStatusCode.OK) return null;
        return (await resp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("accessToken").GetString();
    }

    private static HttpClient Bearer(WebApplicationFactory<Program> factory, string token)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    /// <summary>改动 base64url 段里的一个字符，用来构造签名对不上的令牌。</summary>
    private static string Flip(string segment) =>
        segment[..^1] + (segment[^1] == 'A' ? 'B' : 'A');
}
