using System.Net;
using System.Text;
using System.Text.Json;
using BoltPreload.Api.Endpoints;
using FluentAssertions;
using NpgsqlTypes;
using Xunit;

namespace BoltPreload.Tests.Integration;

/// <summary>
/// 审计日志查询与导出（<c>/api/audit-log</c>）。
/// <para>
/// 出事之后追责就靠这一页，而它出错的方式全都不报错：过滤多筛掉一条、分页提前说没有更多了、
/// 时间窗漏掉边界那条，查的人看到的都是一个干净的结果集，看不出少了东西，
/// 于是"查不到"被当成"没发生过"。
/// </para>
/// <para>
/// CSV 导出还多一层：那份文件要打出来给客户和第三方审查看，备注里一句 <c>=HYPERLINK(...)</c>
/// 在 Excel 里会被当公式执行。<c>CsvExport.Field</c> 有单元测试，但端点有没有接上它没人测过。
/// </para>
/// </summary>
[Collection("postgres")]
public sealed class AuditLogQueryIntegrationTests
{
    private readonly PostgresFixture _fx;
    public AuditLogQueryIntegrationTests(PostgresFixture fx) => _fx = fx;

    // 库里的 audit_log 混着其它用例写进去的行，每个用例用自己的 targetType 圈出自己的数据。
    // targetType 是 varchar(32)，这个前缀留足余量。
    private static string NewScope() => "t_" + Guid.NewGuid().ToString("N")[..12];

    [PostgresFact]
    public async Task Query_HasMore_FlipsExactlyAtTheLastPage()
    {
        _fx.EnsureAvailable();
        var scope = NewScope();
        for (var i = 0; i < 3; i++) await InsertAsync("alarm_ack", scope);

        using var factory = TestApi.CreateFactory(_fx);
        using var client = TestApi.AuthedClient(factory, await _fx.SeedUserAsync("reviewer"), "reviewer");

        // 差一条就到底：hasMore 必须还是 true
        var page1 = await QueryAsync(client, $"targetType={scope}&limit=2");
        page1.GetProperty("count").GetInt32().Should().Be(2);
        page1.GetProperty("hasMore").GetBoolean().Should().BeTrue();

        // 正好取完：hasMore 必须翻成 false。多取一条判定的写法在这个边界最容易差一位，
        // 判成 true 会让查的人翻到一页空的，判成 false 会让他在还有数据时就停手。
        var exact = await QueryAsync(client, $"targetType={scope}&limit=3");
        exact.GetProperty("count").GetInt32().Should().Be(3);
        exact.GetProperty("hasMore").GetBoolean().Should().BeFalse();

        // 翻到最后一页
        var tail = await QueryAsync(client, $"targetType={scope}&limit=2&offset=2");
        tail.GetProperty("count").GetInt32().Should().Be(1);
        tail.GetProperty("hasMore").GetBoolean().Should().BeFalse();
        tail.GetProperty("offset").GetInt32().Should().Be(2);
    }

    [PostgresFact]
    public async Task Query_ActionPrefix_TreatsLikeWildcardsAsLiterals()
    {
        _fx.EnsureAvailable();
        var scope = NewScope();
        await InsertAsync("alarm_ack", scope);
        await InsertAsync("alarmXack", scope);
        await InsertAsync("config_change_approve", scope);

        using var factory = TestApi.CreateFactory(_fx);
        using var client = TestApi.AuthedClient(factory, await _fx.SeedUserAsync("reviewer"), "reviewer");

        // 下划线在 LIKE 里是"任意一个字符"。不转义的话查 alarm_ 会连 alarmXack 一起捞出来——
        // 追责时多出来的行和少掉的行一样糟：它把不相干的人也算进了这次事故。
        var byPrefix = await ActionsOfAsync(client, $"targetType={scope}&actionPrefix=alarm_");
        byPrefix.Should().BeEquivalentTo(["alarm_ack"]);

        // 百分号同理，不转义就是"匹配全部"，一个本该落空的查询会返回满屏结果
        var byPercent = await QueryAsync(client, $"targetType={scope}&actionPrefix=%25");
        byPercent.GetProperty("count").GetInt32().Should().Be(0, "% 是要当字面量找的，不是通配");
    }

    [PostgresFact]
    public async Task Query_TimeWindow_IncludesBothEndpoints()
    {
        _fx.EnsureAvailable();
        var scope = NewScope();
        var baseAt = new DateTimeOffset(2001, 3, 4, 5, 6, 7, TimeSpan.Zero);
        await InsertAsync("alarm_ack", scope, at: baseAt);
        await InsertAsync("alarm_clear", scope, at: baseAt.AddHours(1));
        await InsertAsync("alarm_shelve", scope, at: baseAt.AddHours(2));

        using var factory = TestApi.CreateFactory(_fx);
        using var client = TestApi.AuthedClient(factory, await _fx.SeedUserAsync("reviewer"), "reviewer");

        // 两端都含：查"某时刻到某时刻"时，端点上那一条必须在里面。
        // 若 to 判成开区间，按整点导出的归档件会稳定地漏掉窗口最后一条记录。
        var mid = baseAt.AddHours(1);
        var single = await ActionsOfAsync(client, $"targetType={scope}&from={Iso(mid)}&to={Iso(mid)}");
        single.Should().BeEquivalentTo(["alarm_clear"]);

        // 顺序也是约定的一部分：审计页按时间倒序读，最新的动作要在最前面
        var whole = await ActionsOfAsync(client, $"targetType={scope}&from={Iso(baseAt)}&to={Iso(baseAt.AddHours(2))}");
        whole.Should().Equal("alarm_shelve", "alarm_clear", "alarm_ack");

        var beforeAll = await QueryAsync(client, $"targetType={scope}&to={Iso(baseAt.AddSeconds(-1))}");
        beforeAll.GetProperty("count").GetInt32().Should().Be(0);
    }

    [PostgresFact]
    public async Task Query_UserAndTargetFilters_NarrowToExactRows()
    {
        _fx.EnsureAvailable();
        var scope = NewScope();
        var alice = await _fx.SeedUserAsync("operator");
        var bob = await _fx.SeedUserAsync("operator");
        await InsertAsync("alarm_ack", scope, targetId: 11, userId: alice);
        await InsertAsync("alarm_clear", scope, targetId: 22, userId: bob);
        await InsertAsync("alarm_shelve", scope, targetId: 11, userId: bob);

        using var factory = TestApi.CreateFactory(_fx);
        using var client = TestApi.AuthedClient(factory, await _fx.SeedUserAsync("reviewer"), "reviewer");

        (await ActionsOfAsync(client, $"targetType={scope}&userId={alice}"))
            .Should().BeEquivalentTo(["alarm_ack"], "按人追责时不能把别人的动作算进来");
        (await ActionsOfAsync(client, $"targetType={scope}&targetId=11"))
            .Should().BeEquivalentTo(["alarm_shelve", "alarm_ack"]);
        (await ActionsOfAsync(client, $"targetType={scope}&targetId=11&userId={bob}"))
            .Should().BeEquivalentTo(["alarm_shelve"], "多个条件要叠加收窄，不能其中一个被忽略");
        (await ActionsOfAsync(client, $"targetType={scope}&action=alarm_clear"))
            .Should().BeEquivalentTo(["alarm_clear"]);

        // 用户名要跟着出来：只有 user_id 的话，查的人还得自己再去对一次账号表
        var row = (await QueryAsync(client, $"targetType={scope}&userId={alice}"))
            .GetProperty("items")[0];
        row.GetProperty("username").GetString().Should().NotBeNullOrEmpty();
        row.GetProperty("actionLabel").GetString().Should().Be(DisplayLabels.AuditAction("alarm_ack"));
    }

    [PostgresFact]
    public async Task ExportCsv_NeutralizesFormulaInjection_AndShipsBomWithChineseHeader()
    {
        _fx.EnsureAvailable();
        var scope = NewScope();
        const string payload = "=HYPERLINK(\"http://attacker.example/x\",\"点我\")";
        await InsertAsync("alarm_ack", scope, note: payload);

        using var factory = TestApi.CreateFactory(_fx);
        using var client = TestApi.AuthedClient(factory, await _fx.SeedUserAsync("reviewer"), "reviewer");

        var resp = await client.GetAsync($"/api/audit-log/export.csv?targetType={scope}");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        resp.Content.Headers.ContentType!.MediaType.Should().Be("text/csv");

        var bom = Encoding.UTF8.GetPreamble();
        var bytes = await resp.Content.ReadAsByteArrayAsync();
        bytes.Take(bom.Length).Should().Equal(bom, "没有 BOM 的话 Excel 打开是一屏乱码");
        var csv = Encoding.UTF8.GetString(bytes, bom.Length, bytes.Length - bom.Length);

        csv.Should().StartWith("序号,时间(UTC),用户编号,用户名,动作,对象类型,对象编号,备注,变更前,变更后");
        // 动作列必须是中文：这份表是给第三方审查看的，不能带着 alarm_ack 这种机器码出门
        csv.Should().Contain(DisplayLabels.AuditAction("alarm_ack")).And.NotContain("alarm_ack");

        // 要害：备注里的公式被前置单引号中和，Excel 打开时是一串文本而不是一个可点的链接
        csv.Should().Contain("\"'=HYPERLINK");
        csv.Should().NotContain(",=HYPERLINK", "未中和的公式会在客户机器上被 Excel 执行");
    }

    [PostgresFact]
    public async Task AllRoutes_RejectOperator_ButServeReviewer()
    {
        _fx.EnsureAvailable();
        var scope = NewScope();
        await InsertAsync("alarm_ack", scope);

        using var factory = TestApi.CreateFactory(_fx);
        var routes = new[] { "/api/audit-log/", "/api/audit-log/actions", "/api/audit-log/export.csv" };

        // 审计轨迹能看到谁改过什么，operator 不该有这个视角；但 reviewer 必须畅通，
        // 一收紧过头，出事时反倒没人查得了。
        using (var op = TestApi.AuthedClient(factory, await _fx.SeedUserAsync("operator"), "operator"))
            foreach (var r in routes)
                (await op.GetAsync(r)).StatusCode.Should().Be(HttpStatusCode.Forbidden, $"operator 不得访问 {r}");

        using (var reviewer = TestApi.AuthedClient(factory, await _fx.SeedUserAsync("reviewer"), "reviewer"))
            foreach (var r in routes)
                (await reviewer.GetAsync(r)).StatusCode.Should().Be(HttpStatusCode.OK, $"reviewer 应能访问 {r}");

        // actions 下拉要带上计数，筛选 UI 靠它显示"这个动作有多少条"
        using var admin = TestApi.AuthedClient(factory, await _fx.SeedUserAsync("admin"), "admin");
        var actions = await JsonOfAsync(await admin.GetAsync("/api/audit-log/actions"));
        var ack = actions.GetProperty("items").EnumerateArray()
            .FirstOrDefault(x => x.GetProperty("action").GetString() == "alarm_ack");
        ack.ValueKind.Should().NotBe(JsonValueKind.Undefined);
        ack.GetProperty("count").GetInt64().Should().BeGreaterThan(0);
        ack.GetProperty("label").GetString().Should().Be(DisplayLabels.AuditAction("alarm_ack"));
    }

    // ===================== 请求与铺数据 =====================

    private static string Iso(DateTimeOffset t) => Uri.EscapeDataString(t.ToString("o"));

    private static async Task<JsonElement> JsonOfAsync(HttpResponseMessage resp)
    {
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        return JsonDocument.Parse(await resp.Content.ReadAsStringAsync()).RootElement.Clone();
    }

    private static async Task<JsonElement> QueryAsync(HttpClient client, string query) =>
        await JsonOfAsync(await client.GetAsync($"/api/audit-log/?{query}"));

    /// <summary>按返回顺序（时间倒序）取出 action 列，断言集合内容用。</summary>
    private static async Task<string[]> ActionsOfAsync(HttpClient client, string query)
    {
        var body = await QueryAsync(client, query);
        return body.GetProperty("items").EnumerateArray()
            .Select(x => x.GetProperty("action").GetString()!)
            .ToArray();
    }

    private async Task InsertAsync(
        string action, string targetType,
        long? targetId = null, long? userId = null, string? note = null, DateTimeOffset? at = null)
    {
        await using var cmd = _fx.DataSource.CreateCommand(@"
            INSERT INTO audit_log (user_id, action, target_type, target_id, note, at)
            VALUES (@uid, @action, @tt, @tid, @note, coalesce(@at, now()))");
        cmd.Parameters.AddWithValue("uid", NpgsqlDbType.Bigint, (object?)userId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("action", NpgsqlDbType.Varchar, action);
        cmd.Parameters.AddWithValue("tt", NpgsqlDbType.Varchar, targetType);
        cmd.Parameters.AddWithValue("tid", NpgsqlDbType.Bigint, (object?)targetId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("note", NpgsqlDbType.Text, (object?)note ?? DBNull.Value);
        cmd.Parameters.AddWithValue("at", NpgsqlDbType.TimestampTz, (object?)at ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync();
    }
}
