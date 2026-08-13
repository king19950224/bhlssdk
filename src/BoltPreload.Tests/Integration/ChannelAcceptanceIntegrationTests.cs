using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using NpgsqlTypes;
using Xunit;

namespace BoltPreload.Tests.Integration;

/// <summary>
/// 通道接入验收（<c>/api/devices/{id}/channels/{no}/accept</c> 与验收清单、报告导出）。
/// <para>
/// 验收记录是竣工资料，客户和第三方拿它核对"哪一路通道接的是哪个位置、谁验的、当时什么信号"。
/// 所以要守的是"声明验证过就得真的验证过"：sequential/loopcheck 两种方式必须在验收当时读到回波，
/// 读不到只能落 manual。这道判定要是漏了，一条根本没接好的通道会带着"顺序接入已验证"进资料，
/// 而且 accepted_tof_ns 存了个假指纹，运行期 wiring_suspect 守卫也跟着失效。
/// </para>
/// <para>整组端点此前零 HTTP 覆盖。会话开启依赖真机连接，这里只覆盖它的前置守卫与权限。</para>
/// </summary>
[Collection("postgres")]
public sealed class ChannelAcceptanceIntegrationTests
{
    private const short Ch = 5;   // SeedCalibratableChannelAsync 铺的通道号
    private readonly PostgresFixture _fx;
    public ChannelAcceptanceIntegrationTests(PostgresFixture fx) => _fx = fx;

    [PostgresFact]
    public async Task Accept_BySignalVerification_RefusesWhenTheEchoIsStale()
    {
        _fx.EnsureAvailable();
        var seed = await _fx.SeedCalibratableChannelAsync("过期回波");
        // 5 分钟前有过信号，但验收看的是近 60 秒——此刻电缆可能已经拔了
        await SeedEchoAsync(seed, ageSeconds: 300, echoNs: 27_210m);

        using var factory = TestApi.CreateFactory(_fx);
        using var client = TestApi.AuthedClient(factory, await _fx.SeedUserAsync("operator"), "operator");

        foreach (var method in new[] { "sequential", "loopcheck" })
        {
            var resp = await AcceptAsync(client, seed.DeviceId, new { method });

            resp.StatusCode.Should().Be(HttpStatusCode.Conflict,
                $"{method} 是信号验证方式，当时读不到回波就不算验证过");
            (await resp.Content.ReadAsStringAsync()).Should().Contain("无回波信号");
        }

        (await AcceptanceCountAsync(seed.ChannelId)).Should().Be(0, "拒掉了就不能留下验收记录");
        (await AcceptedTofAsync(seed.ChannelId)).Should().BeNull("更不能写下假指纹");
    }

    [PostgresFact]
    public async Task Accept_Sequential_RecordsFingerprintAndAudit()
    {
        _fx.EnsureAvailable();
        var seed = await _fx.SeedCalibratableChannelAsync("顺序接入");
        await SeedEchoAsync(seed, ageSeconds: 5, echoNs: 27_215m);
        var user = await _fx.SeedUserAsync("operator");

        using var factory = TestApi.CreateFactory(_fx);
        using var client = TestApi.AuthedClient(factory, user, "operator");

        var resp = await AcceptAsync(client, seed.DeviceId,
            new { method = "sequential", cableLabel = "L1-03", positionLabel = "法兰A-3点" });

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("tofNs").GetDecimal().Should().Be(27_215m, "验收记录里要带当时的声时快照");
        body.TryGetProperty("warning", out var w).Should().BeTrue();
        w.ValueKind.Should().Be(JsonValueKind.Null, "有信号验证的验收没有警告");

        (await AcceptanceCountAsync(seed.ChannelId)).Should().Be(1);
        (await AcceptedTofAsync(seed.ChannelId)).Should().Be(27_215m,
            "指纹要落到 channels.accepted_tof_ns：运行期 wiring_suspect 拿它当比对基准");
        (await ChannelNameAsync(seed.ChannelId)).Should().Be("法兰A-3点", "位置标签同步成通道名");
        (await AuditCountAsync("channel_accept", seed.ChannelId))
            .Should().Be(1, "验收记录与审计同事务");
    }

    [PostgresFact]
    public async Task Accept_Manual_WithoutSignal_IsAllowedButSaysTheFingerprintIsEmpty()
    {
        _fx.EnsureAvailable();
        var seed = await _fx.SeedCalibratableChannelAsync("人工登记");

        using var factory = TestApi.CreateFactory(_fx);
        using var client = TestApi.AuthedClient(factory, await _fx.SeedUserAsync("operator"), "operator");

        var resp = await AcceptAsync(client, seed.DeviceId, new { method = "manual", cableLabel = "备用-07" });

        resp.StatusCode.Should().Be(HttpStatusCode.OK, "无信号的备用通道要留一条人工登记的出路");
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("warning").GetString().Should().Contain("wiring_suspect",
            "得说清这条没有指纹、运行期不受接线守卫保护，否则以为和其它通道一样受保护");
        (await AcceptedTofAsync(seed.ChannelId)).Should().BeNull("没有信号就不该编一个指纹出来");
        (await AcceptanceCountAsync(seed.ChannelId)).Should().Be(1);
    }

    [PostgresFact]
    public async Task Accept_RejectsUnknownMethodAndOverlongLabels()
    {
        _fx.EnsureAvailable();
        var seed = await _fx.SeedCalibratableChannelAsync("入参校验");
        await SeedEchoAsync(seed, ageSeconds: 5, echoNs: 27_200m);

        using var factory = TestApi.CreateFactory(_fx);
        using var client = TestApi.AuthedClient(factory, await _fx.SeedUserAsync("operator"), "operator");

        // 方式只有三种。放过别的值等于让验收报告上出现一个谁也说不清含义的方式
        (await AcceptAsync(client, seed.DeviceId, new { method = "knock" }))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await AcceptAsync(client, seed.DeviceId, new { method = (string?)null }))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var tooLong = new string('X', 65);
        (await AcceptAsync(client, seed.DeviceId, new { method = "manual", cableLabel = tooLong }))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await AcceptAsync(client, seed.DeviceId, new { method = "manual", positionLabel = tooLong }))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);

        (await AcceptanceCountAsync(seed.ChannelId)).Should().Be(0);
    }

    [PostgresFact]
    public async Task Accept_UnknownChannel_Is404()
    {
        _fx.EnsureAvailable();
        var seed = await _fx.SeedCalibratableChannelAsync("通道不存在");

        using var factory = TestApi.CreateFactory(_fx);
        using var client = TestApi.AuthedClient(factory, await _fx.SeedUserAsync("operator"), "operator");

        var resp = await client.PostAsJsonAsync(
            $"/api/devices/{seed.DeviceId}/channels/44/accept", new { method = "manual" });

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [PostgresFact]
    public async Task AcceptanceList_KeepsUnacceptedChannelsAndCountsCoverage()
    {
        _fx.EnsureAvailable();
        var seed = await _fx.SeedCalibratableChannelAsync("验收清单");
        var other = await SeedExtraChannelAsync(seed.DeviceId, channelNo: 6);

        using var factory = TestApi.CreateFactory(_fx);
        using var client = TestApi.AuthedClient(factory, await _fx.SeedUserAsync("operator"), "operator");
        await AcceptAsync(client, seed.DeviceId, new { method = "manual", cableLabel = "L1-05" });

        var body = await client.GetFromJsonAsync<JsonElement>($"/api/devices/{seed.DeviceId}/acceptance");

        body.GetProperty("total").GetInt32().Should().Be(2);
        body.GetProperty("accepted").GetInt32().Should().Be(1, "覆盖率要如实反映还差几路没验");
        var items = body.GetProperty("items").EnumerateArray().ToArray();
        // 未验收的通道也得列出来。只列已验的话，清单看起来 100% 完成，漏掉的那路没人发现
        items.Should().HaveCount(2);
        items.Single(i => i.GetProperty("channelId").GetInt64() == other)
            .GetProperty("acceptedAt").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [PostgresFact]
    public async Task AcceptanceCsv_MarksUnacceptedAndNeutralizesFormulas()
    {
        _fx.EnsureAvailable();
        var seed = await _fx.SeedCalibratableChannelAsync("验收报告");
        await SeedExtraChannelAsync(seed.DeviceId, channelNo: 6);
        // 设备编号里塞 CRLF：注释行上的引号不转义，能另起一行伪造公式（同 CsvExport.Inline 的用例）
        await SetDeviceCodeAsync(seed.DeviceId, "D-1\r\n=HYPERLINK(\"http://evil\",\"发票\")");

        using var factory = TestApi.CreateFactory(_fx);
        using var client = TestApi.AuthedClient(factory, await _fx.SeedUserAsync("operator"), "operator");
        await AcceptAsync(client, seed.DeviceId, new { method = "manual", cableLabel = "=cmd|'/c calc'!A1" });

        var resp = await client.GetAsync($"/api/devices/{seed.DeviceId}/acceptance/export.csv");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var lines = (await resp.Content.ReadAsStringAsync()).TrimStart('\uFEFF')
            .Split('\n', StringSplitOptions.RemoveEmptyEntries);

        lines.Should().NotContain(l => l.StartsWith('=') || l.StartsWith('+') || l.StartsWith('@'),
            "客户是在 Excel 里打开这份竣工资料的");
        lines[0].Should().StartWith("# 通道接入验收报告").And.Contain("D-1",
            "设备编号原文要留着，只是折成一行");
        lines.Should().Contain(l => l.Contains("未验收"), "没验的通道要在报告上写明未验收，不能只是空着");
    }

    [PostgresFact]
    public async Task Session_ReportsInactive_AndRefusesToStartWithoutALoadedDevice()
    {
        _fx.EnsureAvailable();
        var seed = await _fx.SeedCalibratableChannelAsync("验收会话");

        using var factory = await TestApi.CreateFactoryWithDeviceOutOfRuntimeAsync(_fx, seed.DeviceId);
        using var reviewer = TestApi.AuthedClient(factory, await _fx.SeedUserAsync("reviewer"), "reviewer");

        var url = $"/api/devices/{seed.DeviceId}/acceptance-session";
        var get = await reviewer.GetFromJsonAsync<JsonElement>(url);
        get.GetProperty("active").GetBoolean().Should().BeFalse();

        // 设备没加载进运行时就开会话，会把 MCR 缩到一个永远收不到插拔的通道集上
        (await reviewer.PostAsJsonAsync(url, new { ttlMinutes = 10 }))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);

        var del = await reviewer.DeleteAsync(url);
        del.StatusCode.Should().Be(HttpStatusCode.OK);
        (await del.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("ended").GetBoolean()
            .Should().BeFalse("本来就没有会话，结束操作要如实说没有，不能假装结束了一个");
    }

    [PostgresFact]
    public async Task Session_StartAndEnd_AreReviewerOnly()
    {
        _fx.EnsureAvailable();
        var seed = await _fx.SeedCalibratableChannelAsync("会话权限");

        using var factory = TestApi.CreateFactory(_fx);
        using var operatorClient = TestApi.AuthedClient(factory, await _fx.SeedUserAsync("operator"), "operator");

        var url = $"/api/devices/{seed.DeviceId}/acceptance-session";
        // 缩小轮询集会影响整台设备的采样，属工程师级操作
        (await operatorClient.PostAsJsonAsync(url, new { ttlMinutes = 10 }))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await operatorClient.DeleteAsync(url)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        // 查状态是登录即可，operator 不该被拦
        (await operatorClient.GetAsync(url)).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // ---- helpers ----

    private static Task<HttpResponseMessage> AcceptAsync(HttpClient client, long deviceId, object body) =>
        client.PostAsJsonAsync($"/api/devices/{deviceId}/channels/{Ch}/accept", body);

    /// <summary>给通道插一条 <paramref name="ageSeconds"/> 秒前的回波，验收的信号判定只看近 60 秒。</summary>
    private async Task SeedEchoAsync(IntegrationSeed.CalibratableChannel seed, int ageSeconds, decimal echoNs)
    {
        await using var cmd = _fx.DataSource.CreateCommand(@"
            INSERT INTO measurements (bolt_id, channel_id, ts, ingested_at, echo_time_ns,
                                      gain, status_code, algo_version)
            VALUES (@bolt, @ch, now() - make_interval(secs => @age), now(), @echo, 16, 0, 'test')");
        cmd.Parameters.AddWithValue("bolt", NpgsqlDbType.Bigint, seed.BoltId);
        cmd.Parameters.AddWithValue("ch", NpgsqlDbType.Bigint, seed.ChannelId);
        cmd.Parameters.AddWithValue("age", NpgsqlDbType.Double, (double)ageSeconds);
        cmd.Parameters.AddWithValue("echo", NpgsqlDbType.Numeric, echoNs);
        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>同设备上再加一路没验收的通道，用来确认清单不会把它漏掉。</summary>
    private async Task<long> SeedExtraChannelAsync(long deviceId, short channelNo)
    {
        await using var cmd = _fx.DataSource.CreateCommand(@"
            INSERT INTO channels (device_id, channel_no, hardware_state, business_state, state,
                                  config_source, discovered_at, created_at, updated_at)
            VALUES (@d, @ch, 'healthy', 'unbound', 'manual', 'database', now(), now(), now())
            RETURNING id");
        cmd.Parameters.AddWithValue("d", NpgsqlDbType.Bigint, deviceId);
        cmd.Parameters.AddWithValue("ch", NpgsqlDbType.Smallint, channelNo);
        return (long)(await cmd.ExecuteScalarAsync())!;
    }

    private async Task SetDeviceCodeAsync(long deviceId, string code)
    {
        await using var cmd = _fx.DataSource.CreateCommand("UPDATE devices SET code=@c WHERE id=@id");
        cmd.Parameters.AddWithValue("c", NpgsqlDbType.Varchar, code);
        cmd.Parameters.AddWithValue("id", NpgsqlDbType.Bigint, deviceId);
        await cmd.ExecuteNonQueryAsync();
    }

    private Task<long> AcceptanceCountAsync(long channelId) =>
        _fx.ScalarAsync<long>("SELECT count(*) FROM channel_acceptance WHERE channel_id=@arg", channelId);

    private async Task<decimal?> AcceptedTofAsync(long channelId)
    {
        await using var cmd = _fx.DataSource.CreateCommand(
            "SELECT accepted_tof_ns FROM channels WHERE id=@id");
        cmd.Parameters.AddWithValue("id", NpgsqlDbType.Bigint, channelId);
        return await cmd.ExecuteScalarAsync() is decimal v ? v : null;
    }

    private async Task<string?> ChannelNameAsync(long channelId)
    {
        await using var cmd = _fx.DataSource.CreateCommand("SELECT name FROM channels WHERE id=@id");
        cmd.Parameters.AddWithValue("id", NpgsqlDbType.Bigint, channelId);
        return await cmd.ExecuteScalarAsync() as string;
    }

    private Task<long> AuditCountAsync(string action, long channelId) =>
        _fx.ScalarAsync<long>(
            $"SELECT count(*) FROM audit_log WHERE action='{action}' AND target_type='channel' AND target_id=@arg",
            channelId);
}
