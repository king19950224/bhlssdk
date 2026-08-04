using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using NpgsqlTypes;
using Xunit;

namespace BoltPreload.Tests.Integration;

/// <summary>
/// 点位表导入导出（<c>/api/devices/{id}/wiring-table</c>）。
/// <para>
/// 点位表是台账，通道对应哪个物理位置、哪颗螺栓全靠它。导入必须全或无：半批落库之后没人说得清
/// 哪几行进去了，而后续标定、告警、验收资料全按这份对应关系走，错一行就是把某个位置的读数
/// 记到另一个位置名下。所以这里最要紧的一组用例是"有一行不合格时前面的行也不能留下痕迹"。
/// </para>
/// <para>
/// 规格与等级未知时同样整批拒绝，不能套默认值——屈服强度要喂给 GB 适用范围判定，
/// 静默按 8.8 级的 640 MPa 算会让一颗 10.9 的螺栓看起来远未到屈服。整组端点此前零 HTTP 覆盖。
/// </para>
/// </summary>
[Collection("postgres")]
public sealed class WiringTableIntegrationTests
{
    private readonly PostgresFixture _fx;
    public WiringTableIntegrationTests(PostgresFixture fx) => _fx = fx;

    [PostgresFact]
    public async Task Import_UnknownSpec_LeavesNothingBehind()
    {
        _fx.EnsureAvailable();
        var seed = await _fx.SeedCalibratableChannelAsync("未知规格");
        var free = await SeedExtraChannelAsync(seed.DeviceId, 6);
        var alsoFree = await SeedExtraChannelAsync(seed.DeviceId, 7);

        using var factory = TestApi.CreateFactory(_fx);
        using var client = TestApi.AuthedClient(factory, await _fx.SeedUserAsync("reviewer"), "reviewer");

        // 第一行完全合法，第二行规格没有预设截面积
        var resp = await ImportAsync(client, seed.DeviceId, seed.GroupId,
            new { channelNo = 6, positionLabel = "法兰A-6点", nominalDiam = "M16", grade = "8.8" },
            new { channelNo = 7, positionLabel = "法兰A-7点", nominalDiam = "M99", grade = "8.8" });

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await resp.Content.ReadAsStringAsync()).Should().Contain("无预设截面积");

        // 全或无：合法那行的位置标签和螺栓都不能留下
        (await ChannelNameAsync(free)).Should().BeNull("整批回滚，第一行的位置标注也不该生效");
        (await ChannelNameAsync(alsoFree)).Should().BeNull();
        (await BoltCountAsync(seed.GroupId)).Should().Be(1, "只有种子里那一颗，导入没建出任何螺栓");
    }

    [PostgresFact]
    public async Task Import_UnknownGrade_LeavesNothingBehind()
    {
        _fx.EnsureAvailable();
        var seed = await _fx.SeedCalibratableChannelAsync("未知等级");
        var free = await SeedExtraChannelAsync(seed.DeviceId, 6);

        using var factory = TestApi.CreateFactory(_fx);
        using var client = TestApi.AuthedClient(factory, await _fx.SeedUserAsync("reviewer"), "reviewer");

        var resp = await ImportAsync(client, seed.DeviceId, seed.GroupId,
            new { channelNo = 6, positionLabel = "法兰A-6点", nominalDiam = "M16", grade = "12.9-特制" });

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest,
            "等级决定屈服强度，猜不出来就得退回去问，不能静默套默认值");
        (await resp.Content.ReadAsStringAsync()).Should().Contain("无预设屈服强度");
        (await ChannelNameAsync(free)).Should().BeNull();
        (await BoltCountAsync(seed.GroupId)).Should().Be(1);
    }

    [PostgresFact]
    public async Task Import_MissingChannel_ReportsWhichRowsAndImportsNothing()
    {
        _fx.EnsureAvailable();
        var seed = await _fx.SeedCalibratableChannelAsync("通道缺失");
        var free = await SeedExtraChannelAsync(seed.DeviceId, 6);

        using var factory = TestApi.CreateFactory(_fx);
        using var client = TestApi.AuthedClient(factory, await _fx.SeedUserAsync("reviewer"), "reviewer");

        var resp = await ImportAsync(client, seed.DeviceId, seed.GroupId,
            new { channelNo = 6, positionLabel = "法兰A-6点" },
            new { channelNo = 33, positionLabel = "这一路不存在" });

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        // 要指出是哪几行，否则拿着几十行的表没法找问题在哪
        body.GetProperty("rows").EnumerateArray()
            .Select(r => r.GetProperty("channelNo").GetInt32())
            .Should().Equal(33);
        (await ChannelNameAsync(free)).Should().BeNull();
    }

    [PostgresFact]
    public async Task Import_DuplicateChannelNo_IsRejectedBeforeAnyWrite()
    {
        _fx.EnsureAvailable();
        var seed = await _fx.SeedCalibratableChannelAsync("行内重复");
        var free = await SeedExtraChannelAsync(seed.DeviceId, 6);

        using var factory = TestApi.CreateFactory(_fx);
        using var client = TestApi.AuthedClient(factory, await _fx.SeedUserAsync("reviewer"), "reviewer");

        // 同一通道出现两次，后一行会覆盖前一行，导入结果取决于行序——这种表必须退回去改
        var resp = await ImportAsync(client, seed.DeviceId, seed.GroupId,
            new { channelNo = 6, positionLabel = "法兰A-6点" },
            new { channelNo = 6, positionLabel = "法兰B-6点" });

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await resp.Content.ReadAsStringAsync()).Should().Contain("通道号重复");
        (await ChannelNameAsync(free)).Should().BeNull();
    }

    /// <summary>
    /// 监测组这两道校验要给出能看懂的错，不是 500。
    /// 两处回滚原先都发生在 reader 还开着的时候，Npgsql 抛"命令正在执行中"，
    /// 于是 404 和 400 都变成了"服务器内部错误"——导入的人不知道是组填错了还是跨了项目。
    /// </summary>
    [PostgresFact]
    public async Task Import_BadGroup_SaysWhatIsWrong_InsteadOf500()
    {
        _fx.EnsureAvailable();
        var seed = await _fx.SeedCalibratableChannelAsync("本项目");
        var foreign = await _fx.SeedCalibratableChannelAsync("别的项目");
        await SeedExtraChannelAsync(seed.DeviceId, 6);

        using var factory = TestApi.CreateFactory(_fx);
        using var client = TestApi.AuthedClient(factory, await _fx.SeedUserAsync("reviewer"), "reviewer");
        var row = new { channelNo = 6, positionLabel = "法兰A-6点" };

        // 建档到另一个项目的监测组下，档案就跨项目串了
        var cross = await ImportAsync(client, seed.DeviceId, foreign.GroupId, row);
        cross.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await cross.Content.ReadAsStringAsync()).Should().Contain("跨项目");
        (await BoltCountAsync(foreign.GroupId)).Should().Be(1);

        var missing = await ImportAsync(client, seed.DeviceId, groupId: 999999999, row);
        missing.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await missing.Content.ReadAsStringAsync()).Should().Contain("监测组不存在");
    }

    [PostgresFact]
    public async Task Import_CreatesBoltsAndPositions_AndAudits()
    {
        _fx.EnsureAvailable();
        var seed = await _fx.SeedCalibratableChannelAsync("正常导入");
        var free = await SeedExtraChannelAsync(seed.DeviceId, 6);

        using var factory = TestApi.CreateFactory(_fx);
        using var client = TestApi.AuthedClient(factory, await _fx.SeedUserAsync("reviewer"), "reviewer");

        var resp = await ImportAsync(client, seed.DeviceId, seed.GroupId,
            new
            {
                channelNo = 6, positionLabel = "法兰A-6点", boltCode = "FA-06",
                nominalDiam = "M16", grade = "8.8", targetPreloadN = 100_000, minPreloadN = 80_000
            });

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("updatedPositions").GetInt32().Should().Be(1);
        body.GetProperty("createdBolts").EnumerateArray().Should().HaveCount(1);

        (await ChannelNameAsync(free)).Should().Be("法兰A-6点");
        (await BoltCountAsync(seed.GroupId)).Should().Be(2);
        (await AuditCountAsync("wiring_import", "device", seed.DeviceId))
            .Should().Be(1, "导入汇总与数据变更同事务");
    }

    [PostgresFact]
    public async Task Import_Rerun_UpdatesPositionButDoesNotDoubleTheBolt()
    {
        _fx.EnsureAvailable();
        var seed = await _fx.SeedCalibratableChannelAsync("重复导入");
        var free = await SeedExtraChannelAsync(seed.DeviceId, 6);

        using var factory = TestApi.CreateFactory(_fx);
        using var client = TestApi.AuthedClient(factory, await _fx.SeedUserAsync("reviewer"), "reviewer");

        await ImportAsync(client, seed.DeviceId, seed.GroupId,
            new { channelNo = 6, positionLabel = "法兰A-6点", boltCode = "FA-06", nominalDiam = "M16", grade = "8.8" });

        // 现场改了位置名再导一遍是常规操作，不能因此多出一颗螺栓
        var again = await ImportAsync(client, seed.DeviceId, seed.GroupId,
            new { channelNo = 6, positionLabel = "法兰A-6点(修正)", boltCode = "FA-06", nominalDiam = "M16", grade = "8.8" });

        again.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await again.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("skippedBound").EnumerateArray().Select(x => x.GetInt32())
            .Should().ContainSingle("已绑定的通道只更新位置，跳过建档").Which.Should().Be(6);
        (await ChannelNameAsync(free)).Should().Be("法兰A-6点(修正)");
        (await BoltCountAsync(seed.GroupId)).Should().Be(2);
    }

    [PostgresFact]
    public async Task Import_RejectsEmptyRowsAndMissingGroup()
    {
        _fx.EnsureAvailable();
        var seed = await _fx.SeedCalibratableChannelAsync("入参校验");
        await SeedExtraChannelAsync(seed.DeviceId, 6);

        using var factory = TestApi.CreateFactory(_fx);
        using var client = TestApi.AuthedClient(factory, await _fx.SeedUserAsync("reviewer"), "reviewer");
        var url = $"/api/devices/{seed.DeviceId}/wiring-table/import";

        (await client.PostAsJsonAsync(url, new { rows = Array.Empty<object>() }))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);

        // createBolts 默认为真，此时缺 groupId 就不知道新螺栓该挂哪个监测组
        (await client.PostAsJsonAsync(url, new { rows = new[] { new { channelNo = 6 } } }))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);

        // 只标位置不建档时不需要 groupId
        (await client.PostAsJsonAsync(url, new
        {
            rows = new[] { new { channelNo = 6, positionLabel = "只标位置" } },
            createBolts = false
        })).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [PostgresFact]
    public async Task Import_IsReviewerOnly_AndExportIsReadableByOperator()
    {
        _fx.EnsureAvailable();
        var seed = await _fx.SeedCalibratableChannelAsync("导入权限");
        await SeedExtraChannelAsync(seed.DeviceId, 6);

        using var factory = TestApi.CreateFactory(_fx);
        using var client = TestApi.AuthedClient(factory, await _fx.SeedUserAsync("operator"), "operator");

        // 批量建档改的是台账，operator 不该单独动
        (await ImportAsync(client, seed.DeviceId, seed.GroupId, new { channelNo = 6, positionLabel = "x" }))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await client.GetAsync($"/api/devices/{seed.DeviceId}/wiring-table/export.csv"))
            .StatusCode.Should().Be(HttpStatusCode.OK, "导出是只读的，现场谁都得能拿");
    }

    [PostgresFact]
    public async Task Export_HeaderStaysInTheOrderImportExpects()
    {
        _fx.EnsureAvailable();
        var seed = await _fx.SeedCalibratableChannelAsync("导出表头");

        using var factory = TestApi.CreateFactory(_fx);
        using var client = TestApi.AuthedClient(factory, await _fx.SeedUserAsync("operator"), "operator");

        var resp = await client.GetAsync($"/api/devices/{seed.DeviceId}/wiring-table/export.csv");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var text = (await resp.Content.ReadAsStringAsync()).TrimStart('\uFEFF');
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);

        // 这份表要能导出→改→回导。列序变了前端就按错的列取值，把规格读成等级之类。
        // G7 门禁盯的是表头与前端别名表的对应，这里盯的是表头本身没被悄悄改掉。
        lines[0].Should().Be("通道号,位置,螺栓编号,规格,等级,目标预紧力,报警下限,探头状态,验收时间,螺栓位号");
        lines.Should().HaveCountGreaterThan(1, "种子设备有一路通道，导出不该只有表头");

        (await client.GetAsync("/api/devices/999999999/wiring-table/export.csv"))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ---- helpers ----

    private static Task<HttpResponseMessage> ImportAsync(
        HttpClient client, long deviceId, long groupId, params object[] rows) =>
        client.PostAsJsonAsync($"/api/devices/{deviceId}/wiring-table/import",
            new { rows, groupId, note = "集成测试导入" });

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

    private async Task<string?> ChannelNameAsync(long channelId)
    {
        await using var cmd = _fx.DataSource.CreateCommand("SELECT name FROM channels WHERE id=@id");
        cmd.Parameters.AddWithValue("id", NpgsqlDbType.Bigint, channelId);
        return await cmd.ExecuteScalarAsync() as string;
    }

    private Task<long> BoltCountAsync(long groupId) =>
        _fx.ScalarAsync<long>(
            "SELECT count(*) FROM bolts WHERE group_id=@arg AND deleted_at IS NULL", groupId);

    private Task<long> AuditCountAsync(string action, string targetType, long targetId) =>
        _fx.ScalarAsync<long>(
            $"SELECT count(*) FROM audit_log WHERE action='{action}' AND target_type='{targetType}' AND target_id=@arg",
            targetId);
}
