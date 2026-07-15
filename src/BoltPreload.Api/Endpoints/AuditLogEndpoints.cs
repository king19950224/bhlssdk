using System.Text;
using BoltPreload.Api.Auth;
using Npgsql;
using NpgsqlTypes;

namespace BoltPreload.Api.Endpoints;

/// <summary>
/// 审计日志查询 / 导出（合规审查）。audit_log 是 append-only 永久轨迹，记录标定、参数变更、
/// 告警确认等安全攸关动作的"谁、何时、改了什么"。仅 reviewer / admin 可查阅。
/// </summary>
public static class AuditLogEndpoints
{
    private const string BaseSelect = @"
        SELECT a.id, a.user_id, u.username, a.ip_addr::text, a.action,
               a.target_type, a.target_id, a.before_jsonb::text, a.after_jsonb::text, a.note, a.at
        FROM audit_log a
        LEFT JOIN users u ON u.id = a.user_id
        WHERE 1=1";

    public static IEndpointRouteBuilder MapAuditLogEndpoints(this IEndpointRouteBuilder app)
    {
        var grp = app.MapGroup("/api/audit-log")
            .WithTags("AuditLog")
            .RequireAuthorization(AuthPolicies.ReviewerAccess);

        // 查询（多维过滤 + 分页）
        grp.MapGet("/", async (
            long? userId, string? action, string? actionPrefix, string? targetType, long? targetId,
            DateTimeOffset? from, DateTimeOffset? to, int? limit, int? offset,
            NpgsqlDataSource ds, CancellationToken ct) =>
        {
            var lim = Math.Clamp(limit ?? 100, 1, 500);
            var off = Math.Max(offset ?? 0, 0);

            await using var cmd = ds.CreateCommand();
            var sql = new StringBuilder(BaseSelect);
            ApplyFilters(sql, cmd, userId, action, actionPrefix, targetType, targetId, from, to);
            sql.Append(" ORDER BY a.at DESC LIMIT @limit OFFSET @offset");
            cmd.Parameters.AddWithValue("limit", NpgsqlDbType.Integer, lim + 1); // 多取 1 条判断 hasMore
            cmd.Parameters.AddWithValue("offset", NpgsqlDbType.Integer, off);
            cmd.CommandText = sql.ToString();

            var items = new List<object>();
            await using (var r = await cmd.ExecuteReaderAsync(ct))
            {
                while (await r.ReadAsync(ct))
                {
                    items.Add(new
                    {
                        id = r.GetInt64(0),
                        userId = r.IsDBNull(1) ? (long?)null : r.GetInt64(1),
                        username = r.IsDBNull(2) ? null : r.GetString(2),
                        ipAddr = r.IsDBNull(3) ? null : r.GetString(3),
                        action = r.GetString(4),
                        targetType = r.IsDBNull(5) ? null : r.GetString(5),
                        targetId = r.IsDBNull(6) ? (long?)null : r.GetInt64(6),
                        before = r.IsDBNull(7) ? null : r.GetString(7),
                        after = r.IsDBNull(8) ? null : r.GetString(8),
                        note = r.IsDBNull(9) ? null : r.GetString(9),
                        at = r.GetDateTime(10)
                    });
                }
            }

            var hasMore = items.Count > lim;
            if (hasMore) items.RemoveAt(items.Count - 1);
            return Results.Ok(new { count = items.Count, limit = lim, offset = off, hasMore, items });
        })
        .WithName("QueryAuditLog")
        .WithSummary("查询审计日志（filters: userId/action/actionPrefix/targetType/targetId/from/to）");

        // 可选 action 列表（给筛选 UI 下拉）
        grp.MapGet("/actions", async (NpgsqlDataSource ds, CancellationToken ct) =>
        {
            await using var cmd = ds.CreateCommand(
                "SELECT action, COUNT(*) FROM audit_log GROUP BY action ORDER BY action");
            var items = new List<object>();
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
                items.Add(new { action = r.GetString(0), count = r.GetInt64(1) });
            return Results.Ok(new { items });
        })
        .WithName("AuditLogActions")
        .WithSummary("审计动作类型 + 计数（筛选用）");

        // CSV 导出（合规归档）。带 UTF-8 BOM 方便 Excel 正确显示中文。
        grp.MapGet("/export.csv", async (
            long? userId, string? action, string? actionPrefix, string? targetType, long? targetId,
            DateTimeOffset? from, DateTimeOffset? to, int? limit,
            NpgsqlDataSource ds, CancellationToken ct) =>
        {
            var lim = Math.Clamp(limit ?? 5000, 1, 50000);

            await using var cmd = ds.CreateCommand();
            var sql = new StringBuilder(BaseSelect);
            ApplyFilters(sql, cmd, userId, action, actionPrefix, targetType, targetId, from, to);
            sql.Append(" ORDER BY a.at DESC LIMIT @limit");
            cmd.Parameters.AddWithValue("limit", NpgsqlDbType.Integer, lim);
            cmd.CommandText = sql.ToString();

            var csv = new StringBuilder();
            csv.Append('\uFEFF'); // UTF-8 BOM
            csv.AppendLine("id,at_utc,user_id,username,action,target_type,target_id,note,before,after");
            await using (var r = await cmd.ExecuteReaderAsync(ct))
            {
                while (await r.ReadAsync(ct))
                {
                    csv.Append(r.GetInt64(0)).Append(',')
                       .Append(Csv(r.GetDateTime(10).ToString("o"))).Append(',')
                       .Append(r.IsDBNull(1) ? "" : r.GetInt64(1).ToString()).Append(',')
                       .Append(Csv(r.IsDBNull(2) ? "" : r.GetString(2))).Append(',')
                       .Append(Csv(r.GetString(4))).Append(',')
                       .Append(Csv(r.IsDBNull(5) ? "" : r.GetString(5))).Append(',')
                       .Append(r.IsDBNull(6) ? "" : r.GetInt64(6).ToString()).Append(',')
                       .Append(Csv(r.IsDBNull(9) ? "" : r.GetString(9))).Append(',')
                       .Append(Csv(r.IsDBNull(7) ? "" : r.GetString(7))).Append(',')
                       .Append(Csv(r.IsDBNull(8) ? "" : r.GetString(8))).Append('\n');
                }
            }

            var bytes = Encoding.UTF8.GetBytes(csv.ToString());
            return Results.File(bytes, "text/csv; charset=utf-8",
                $"audit-log-{DateTime.UtcNow:yyyyMMddHHmmss}.csv");
        })
        .WithName("ExportAuditLogCsv")
        .WithSummary("导出审计日志 CSV（合规归档）");

        return app;
    }

    private static void ApplyFilters(
        StringBuilder sql, NpgsqlCommand cmd,
        long? userId, string? action, string? actionPrefix, string? targetType, long? targetId,
        DateTimeOffset? from, DateTimeOffset? to)
    {
        if (userId is not null) { sql.Append(" AND a.user_id=@user_id"); cmd.Parameters.AddWithValue("user_id", NpgsqlDbType.Bigint, userId.Value); }
        if (!string.IsNullOrWhiteSpace(action)) { sql.Append(" AND a.action=@action"); cmd.Parameters.AddWithValue("action", NpgsqlDbType.Varchar, action); }
        if (!string.IsNullOrWhiteSpace(actionPrefix))
        {
            // 转义 LIKE 通配符：actionPrefix 是"前缀"语义，传 '%'/'_' 不应变成任意匹配（Postgres 默认转义符 \）
            var escaped = actionPrefix.Replace(@"\", @"\\").Replace("%", @"\%").Replace("_", @"\_");
            sql.Append(" AND a.action LIKE @ap");
            cmd.Parameters.AddWithValue("ap", NpgsqlDbType.Varchar, escaped + "%");
        }
        if (!string.IsNullOrWhiteSpace(targetType)) { sql.Append(" AND a.target_type=@tt"); cmd.Parameters.AddWithValue("tt", NpgsqlDbType.Varchar, targetType); }
        if (targetId is not null) { sql.Append(" AND a.target_id=@tid"); cmd.Parameters.AddWithValue("tid", NpgsqlDbType.Bigint, targetId.Value); }
        if (from is not null) { sql.Append(" AND a.at >= @from"); cmd.Parameters.AddWithValue("from", NpgsqlDbType.TimestampTz, from.Value); }
        if (to is not null) { sql.Append(" AND a.at <= @to"); cmd.Parameters.AddWithValue("to", NpgsqlDbType.TimestampTz, to.Value); }
    }

    private static string Csv(string? value)
    {
        if (string.IsNullOrEmpty(value)) return "";
        // CSV 公式注入防护：note 等字段含用户输入（告警备注/驳回原因），
        // 以 = + - @ 开头的值在 Excel 打开时会被当公式执行，前置单引号中和
        if (value[0] is '=' or '+' or '-' or '@') value = "'" + value;
        // 始终加引号并把内部引号翻倍，避免逗号/换行/引号破坏列
        return "\"" + value.Replace("\"", "\"\"") + "\"";
    }
}
