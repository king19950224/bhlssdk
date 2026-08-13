using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using NpgsqlTypes;
using Xunit;

namespace BoltPreload.Tests.Integration;

/// <summary>
/// 通道自动检测的 HTTP 层（<c>/api/devices/{id}/channels/probe-scan</c>）。
/// <para>
/// 扫描会逐通道切 SingleMeas，整台设备的业务监测在此期间中断，所以入参校验不是形式：
/// 48 通道 × 60 秒采样就是四十多分钟不出数。判定算法本身有
/// <c>ProbeScanOrchestratorTests</c> 覆盖，这里管的是校验、跨设备越权取数和接线。
/// </para>
/// <para>
/// 启动需要真机在运行时里，所以成功路径不在这一层测；能测的是它失败时不留下 pending 记录
/// ——留下了 <c>FindOpenRunAsync</c> 就会一直认为有扫描在跑，这台设备之后再也启动不了扫描。
/// 整组端点此前零 HTTP 覆盖。
/// </para>
/// </summary>
[Collection("postgres")]
public sealed class ProbeScanEndpointsIntegrationTests
{
    private readonly PostgresFixture _fx;
    public ProbeScanEndpointsIntegrationTests(PostgresFixture fx) => _fx = fx;

    [PostgresFact]
    public async Task Start_RejectsChannelsWindowsAndStrategyOutOfRange()
    {
        _fx.EnsureAvailable();
        var seed = await _fx.SeedCalibratableChannelAsync("扫描入参");

        using var factory = TestApi.CreateFactory(_fx);
        using var client = TestApi.AuthedClient(factory, await _fx.SeedUserAsync("operator"), "operator");

        // 逐条对错误文案，不只对状态码：设备没在运行时里也返回 400，
        // 光看状态码的话把这些校验整段删掉测试照样绿
        (object body, string says)[] bad =
        [
            (new { channels = new[] { 49 } }, "通道号超出"),   // 超出单板 48 通道上限
            (new { channels = new[] { 1 }, settleSeconds = 31 }, "settleSeconds"),
            (new { channels = new[] { 1 }, sampleSeconds = 61 }, "sampleSeconds"),
            (new { channels = new[] { 1 }, sampleSeconds = 0 }, "sampleSeconds"),
            (new { channels = new[] { 1 }, strategy = "mcr_all" }, "未知 strategy"),
        ];
        foreach (var (body, says) in bad)
        {
            var resp = await StartAsync(client, seed.DeviceId, body);
            resp.StatusCode.Should().Be(HttpStatusCode.BadRequest, $"入参 {body} 应被拒");
            (await resp.Content.ReadAsStringAsync()).Should().Contain(says,
                $"要说清是哪个字段不对，{body} 的报错却是另一码事");
        }

        (await RunCountAsync(seed.DeviceId)).Should().Be(0, "校验没过就不该留下扫描记录");
    }

    [PostgresFact]
    public async Task Start_ZeroChannelNumbersAreDroppedThenReportedAsEmpty()
    {
        _fx.EnsureAvailable();
        var seed = await _fx.SeedCalibratableChannelAsync("通道号为零");

        using var factory = TestApi.CreateFactory(_fx);
        using var client = TestApi.AuthedClient(factory, await _fx.SeedUserAsync("operator"), "operator");

        // 0 会被 Where(c => c > 0) 滤掉，滤完为空要明确报错，不能悄悄退回"扫全部通道"
        var resp = await StartAsync(client, seed.DeviceId, new { channels = new[] { 0 } });

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await resp.Content.ReadAsStringAsync()).Should().Contain("channels 不能为空");
        (await RunCountAsync(seed.DeviceId)).Should().Be(0);
    }

    [PostgresFact]
    public async Task Start_WithoutALoadedDevice_LeavesNoPendingRun()
    {
        _fx.EnsureAvailable();
        var seed = await _fx.SeedCalibratableChannelAsync("设备未加载");

        using var factory = await TestApi.CreateFactoryWithDeviceOutOfRuntimeAsync(_fx, seed.DeviceId);
        using var client = TestApi.AuthedClient(factory, await _fx.SeedUserAsync("operator"), "operator");

        var resp = await StartAsync(client, seed.DeviceId, new { channels = new[] { 1, 2 } });

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await resp.Content.ReadAsStringAsync()).Should().Contain("运行时");
        (await RunCountAsync(seed.DeviceId)).Should().Be(0,
            "启动失败留下 pending 行的话，FindOpenRunAsync 会一直认为有扫描在跑，这台设备再也扫不了");
    }

    [PostgresFact]
    public async Task Latest_Is404BeforeTheFirstScan_AndReturnsItAfterwards()
    {
        _fx.EnsureAvailable();
        var seed = await _fx.SeedCalibratableChannelAsync("最近一次扫描");
        var url = $"/api/devices/{seed.DeviceId}/channels/probe-scan/latest";

        using var factory = TestApi.CreateFactory(_fx);
        using var client = TestApi.AuthedClient(factory, await _fx.SeedUserAsync("operator"), "operator");

        (await client.GetAsync(url)).StatusCode.Should().Be(HttpStatusCode.NotFound,
            "没扫过就是没扫过，不能返回一个空壳结果让前端显示成扫完了没发现探头");

        var runId = await SeedRunAsync(seed.DeviceId, status: "completed");
        var body = await client.GetFromJsonAsync<JsonElement>(url);
        body.GetProperty("scanRunId").GetInt64().Should().Be(runId);
        body.GetProperty("status").GetString().Should().Be("completed");
    }

    [PostgresFact]
    public async Task Get_RunBelongingToAnotherDevice_IsRefused()
    {
        _fx.EnsureAvailable();
        var mine = await _fx.SeedCalibratableChannelAsync("本设备");
        var other = await _fx.SeedCalibratableChannelAsync("别的设备");
        var otherRun = await SeedRunAsync(other.DeviceId, status: "completed");

        using var factory = TestApi.CreateFactory(_fx);
        using var client = TestApi.AuthedClient(factory, await _fx.SeedUserAsync("operator"), "operator");

        // scanRunId 是全局自增的，猜得到。路径上的 deviceId 必须参与校验，
        // 否则拿着别的设备的 id 就能读到那台设备的通道判定结果
        var resp = await client.GetAsync(
            $"/api/devices/{mine.DeviceId}/channels/probe-scan/{otherRun}");

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await resp.Content.ReadAsStringAsync()).Should().Contain("不属于此设备");

        // 同一条记录从它自己的设备下取是正常的
        (await client.GetAsync($"/api/devices/{other.DeviceId}/channels/probe-scan/{otherRun}"))
            .StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [PostgresFact]
    public async Task Get_UnknownRun_Is404()
    {
        _fx.EnsureAvailable();
        var seed = await _fx.SeedCalibratableChannelAsync("扫描不存在");

        using var factory = TestApi.CreateFactory(_fx);
        using var client = TestApi.AuthedClient(factory, await _fx.SeedUserAsync("operator"), "operator");

        (await client.GetAsync($"/api/devices/{seed.DeviceId}/channels/probe-scan/999999999"))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [PostgresFact]
    public async Task Abort_WhenNothingIsRunning_Is404()
    {
        _fx.EnsureAvailable();
        var seed = await _fx.SeedCalibratableChannelAsync("中止扫描");
        // 库里有条 running 记录，但进程里没有在跑的扫描（重启后的残留就是这样）
        var runId = await SeedRunAsync(seed.DeviceId, status: "running");

        using var factory = TestApi.CreateFactory(_fx);
        using var client = TestApi.AuthedClient(factory, await _fx.SeedUserAsync("operator"), "operator");

        var resp = await client.PostAsync(
            $"/api/devices/{seed.DeviceId}/channels/probe-scan/{runId}/abort", null);

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound,
            "中止的是进程里正在跑的那次；没有就如实说没找到，不能回 aborted=true 让人以为已经停了");
    }

    // ---- helpers ----

    private static Task<HttpResponseMessage> StartAsync(HttpClient client, long deviceId, object body) =>
        client.PostAsJsonAsync($"/api/devices/{deviceId}/channels/probe-scan", body);

    private async Task<long> SeedRunAsync(long deviceId, string status)
    {
        await using var cmd = _fx.DataSource.CreateCommand(@"
            INSERT INTO channel_probe_scan_runs (
                device_id, status, strategy, requested_channels,
                settle_seconds, sample_seconds, thresholds_jsonb)
            VALUES (@d, @st, 'single_meas', @chs, 2, 5, '{}'::jsonb)
            RETURNING id");
        cmd.Parameters.AddWithValue("d", NpgsqlDbType.Bigint, deviceId);
        cmd.Parameters.AddWithValue("st", NpgsqlDbType.Varchar, status);
        cmd.Parameters.AddWithValue("chs", NpgsqlDbType.Array | NpgsqlDbType.Smallint, new short[] { 5 });
        return (long)(await cmd.ExecuteScalarAsync())!;
    }

    private Task<long> RunCountAsync(long deviceId) =>
        _fx.ScalarAsync<long>(
            "SELECT count(*) FROM channel_probe_scan_runs WHERE device_id=@arg", deviceId);
}
