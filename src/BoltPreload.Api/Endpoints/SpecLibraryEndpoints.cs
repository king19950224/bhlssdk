using BoltPreload.Api.Auth;
using BoltPreload.Domain;
using Npgsql;
using NpgsqlTypes;

namespace BoltPreload.Api.Endpoints;

/// <summary>材料创建/更新请求（PATCH 时全部可空 = 不改）。</summary>
public sealed record MaterialSpecRequest(
    string? Code = null,
    string? Name = null,
    decimal? SoundVelocityMs = null,
    decimal? TempCoefficientPerC = null,
    decimal? AcousticElasticFactor = null,
    decimal? ElasticModulusGpa = null,
    string? Note = null,
    bool? Enabled = null);

/// <summary>规格预设创建/更新请求。</summary>
public sealed record BoltSpecPresetRequest(
    string? Name = null,
    string? NominalDiam = null,
    string? Grade = null,
    string? MaterialCode = null,
    decimal? CrossSectionMm2 = null,
    decimal? Rp02Mpa = null,
    decimal? EffectiveLengthMm = null,
    decimal? TotalLengthMm = null,
    decimal? TargetPreloadN = null,
    decimal? MinPreloadN = null,
    string? Note = null,
    bool? Enabled = null);

/// <summary>
/// 参数字典（material-library-design）：材料声学库 + 螺栓规格预设 + 参数展开器。
/// <para>
/// 展开器（resolve）是唯一的推导逻辑入口：建档、改规格、导入都用它，防止三处各算一套。
/// 国标值（等级→Rp0.2、螺纹→As）查 <see cref="BoltStandards"/> 代码表，材料值查库。
/// 材料/预设只做 enabled 软停用不做删除——被历史建档引用过的参数来源必须可追。
/// </para>
/// </summary>
public static class SpecLibraryEndpoints
{
    public static IEndpointRouteBuilder MapSpecLibraryEndpoints(this IEndpointRouteBuilder app)
    {
        var grp = app.MapGroup("/api/spec-library").WithTags("SpecLibrary");

        // ---- 材料库 ----
        grp.MapGet("/materials", async (NpgsqlDataSource ds, CancellationToken ct) =>
        {
            var items = new List<object>();
            await using var cmd = ds.CreateCommand(@"
                SELECT id, code, name, sound_velocity_ms, temp_coefficient_per_c,
                       acoustic_elastic_factor, elastic_modulus_gpa, note, enabled
                FROM material_specs ORDER BY enabled DESC, code");
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
                items.Add(new
                {
                    id = r.GetInt64(0), code = r.GetString(1), name = r.GetString(2),
                    soundVelocityMs = r.GetDecimal(3), tempCoefficientPerC = r.GetDecimal(4),
                    acousticElasticFactor = r.GetDecimal(5), elasticModulusGpa = r.GetDecimal(6),
                    note = r.IsDBNull(7) ? null : r.GetString(7), enabled = r.GetBoolean(8),
                });
            return Results.Ok(new { count = items.Count, items });
        })
        .RequireAuthorization(AuthPolicies.LoggedIn)
        .WithName("ListMaterialSpecs")
        .WithSummary("材料声学库列表");

        grp.MapPost("/materials", async (
            MaterialSpecRequest? body, HttpContext http, NpgsqlDataSource ds, CancellationToken ct) =>
        {
            var user = UserContext.Require(http);
            if (body is null || string.IsNullOrWhiteSpace(body.Code) || string.IsNullOrWhiteSpace(body.Name))
                return Results.BadRequest(new { error = "code / name 必填" });
            if (body.SoundVelocityMs is not > 0 || body.ElasticModulusGpa is not > 0 || body.TempCoefficientPerC is not > 0)
                return Results.BadRequest(new { error = "声速 / 弹性模量 / 温度系数 必填且为正数" });

            try
            {
                await using var conn = await ds.OpenConnectionAsync(ct);
                await using var tx = await conn.BeginTransactionAsync(ct);
                long id;
                await using (var cmd = new NpgsqlCommand(@"
                    INSERT INTO material_specs
                        (code, name, sound_velocity_ms, temp_coefficient_per_c, acoustic_elastic_factor, elastic_modulus_gpa, note)
                    VALUES (@code, @name, @v, @tc, @k, @e, @note)
                    RETURNING id", conn, tx))
                {
                    cmd.Parameters.AddWithValue("code", NpgsqlDbType.Varchar, body.Code.Trim());
                    cmd.Parameters.AddWithValue("name", NpgsqlDbType.Varchar, body.Name.Trim());
                    cmd.Parameters.AddWithValue("v", NpgsqlDbType.Numeric, body.SoundVelocityMs.Value);
                    cmd.Parameters.AddWithValue("tc", NpgsqlDbType.Numeric, body.TempCoefficientPerC.Value);
                    cmd.Parameters.AddWithValue("k", NpgsqlDbType.Numeric, body.AcousticElasticFactor ?? 2.5m);
                    cmd.Parameters.AddWithValue("e", NpgsqlDbType.Numeric, body.ElasticModulusGpa.Value);
                    cmd.Parameters.AddWithValue("note", NpgsqlDbType.Text, (object?)body.Note ?? DBNull.Value);
                    id = (long)(await cmd.ExecuteScalarAsync(ct) ?? 0L);
                }
                await AuditAsync(conn, tx, user.UserId, "material_spec_create", id,
                    $"新建材料 {body.Code}（声速 {body.SoundVelocityMs} m/s，E {body.ElasticModulusGpa} GPa）", ct);
                await tx.CommitAsync(ct);
                return Results.Ok(new { id, code = body.Code.Trim() });
            }
            catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
            {
                return Results.Conflict(new { error = $"材料 code 已存在：{body.Code}" });
            }
        })
        .RequireAuthorization(AuthPolicies.AdminAccess)
        .WithName("CreateMaterialSpec")
        .WithSummary("新建材料（admin，审计）");

        grp.MapPatch("/materials/{id:long}", async (
            long id, MaterialSpecRequest? body, HttpContext http, NpgsqlDataSource ds, CancellationToken ct) =>
        {
            var user = UserContext.Require(http);
            if (body is null) return Results.BadRequest(new { error = "请求体为空" });
            if (body.SoundVelocityMs is <= 0 || body.ElasticModulusGpa is <= 0 || body.TempCoefficientPerC is <= 0 || body.AcousticElasticFactor is <= 0)
                return Results.BadRequest(new { error = "数值字段必须为正数" });

            await using var conn = await ds.OpenConnectionAsync(ct);
            await using var tx = await conn.BeginTransactionAsync(ct);
            string? code = null;
            await using (var cmd = new NpgsqlCommand(@"
                UPDATE material_specs SET
                    name = COALESCE(@name, name),
                    sound_velocity_ms = COALESCE(@v, sound_velocity_ms),
                    temp_coefficient_per_c = COALESCE(@tc, temp_coefficient_per_c),
                    acoustic_elastic_factor = COALESCE(@k, acoustic_elastic_factor),
                    elastic_modulus_gpa = COALESCE(@e, elastic_modulus_gpa),
                    note = COALESCE(@note, note),
                    enabled = COALESCE(@enabled, enabled),
                    updated_at = now()
                WHERE id = @id
                RETURNING code", conn, tx))
            {
                cmd.Parameters.AddWithValue("id", NpgsqlDbType.Bigint, id);
                cmd.Parameters.AddWithValue("name", NpgsqlDbType.Varchar, (object?)body.Name ?? DBNull.Value);
                cmd.Parameters.AddWithValue("v", NpgsqlDbType.Numeric, (object?)body.SoundVelocityMs ?? DBNull.Value);
                cmd.Parameters.AddWithValue("tc", NpgsqlDbType.Numeric, (object?)body.TempCoefficientPerC ?? DBNull.Value);
                cmd.Parameters.AddWithValue("k", NpgsqlDbType.Numeric, (object?)body.AcousticElasticFactor ?? DBNull.Value);
                cmd.Parameters.AddWithValue("e", NpgsqlDbType.Numeric, (object?)body.ElasticModulusGpa ?? DBNull.Value);
                cmd.Parameters.AddWithValue("note", NpgsqlDbType.Text, (object?)body.Note ?? DBNull.Value);
                cmd.Parameters.AddWithValue("enabled", NpgsqlDbType.Boolean, (object?)body.Enabled ?? DBNull.Value);
                code = await cmd.ExecuteScalarAsync(ct) as string;
            }
            if (code is null) { await tx.RollbackAsync(ct); return Results.NotFound(new { error = "材料不存在", id }); }
            await AuditAsync(conn, tx, user.UserId, "material_spec_update", id,
                $"修改材料 {code}（改动字段按请求体，历史螺栓不追溯）", ct);
            await tx.CommitAsync(ct);
            return Results.Ok(new { id, code, updated = true });
        })
        .RequireAuthorization(AuthPolicies.AdminAccess)
        .WithName("UpdateMaterialSpec")
        .WithSummary("修改/停用材料（admin，审计；不追溯已建档螺栓）");

        // ---- 规格预设 ----
        grp.MapGet("/presets", async (NpgsqlDataSource ds, CancellationToken ct) =>
        {
            var items = new List<object>();
            await using var cmd = ds.CreateCommand(@"
                SELECT p.id, p.name, p.nominal_diam, p.grade, p.material_code,
                       p.cross_section_mm2, p.rp02_mpa, p.effective_length_mm, p.total_length_mm,
                       p.target_preload_n, p.min_preload_n, p.note, p.enabled
                FROM bolt_spec_presets p ORDER BY p.enabled DESC, p.name");
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
                items.Add(new
                {
                    id = r.GetInt64(0), name = r.GetString(1),
                    nominalDiam = r.GetString(2), grade = r.GetString(3), materialCode = r.GetString(4),
                    crossSectionMm2 = r.IsDBNull(5) ? (decimal?)null : r.GetDecimal(5),
                    rp02Mpa = r.IsDBNull(6) ? (decimal?)null : r.GetDecimal(6),
                    effectiveLengthMm = r.GetDecimal(7), totalLengthMm = r.GetDecimal(8),
                    targetPreloadN = r.IsDBNull(9) ? (decimal?)null : r.GetDecimal(9),
                    minPreloadN = r.IsDBNull(10) ? (decimal?)null : r.GetDecimal(10),
                    note = r.IsDBNull(11) ? null : r.GetString(11), enabled = r.GetBoolean(12),
                });
            return Results.Ok(new { count = items.Count, items });
        })
        .RequireAuthorization(AuthPolicies.LoggedIn)
        .WithName("ListBoltSpecPresets")
        .WithSummary("规格预设列表");

        grp.MapPost("/presets", async (
            BoltSpecPresetRequest? body, HttpContext http, NpgsqlDataSource ds, CancellationToken ct) =>
        {
            var user = UserContext.Require(http);
            if (body is null || string.IsNullOrWhiteSpace(body.Name)
                || string.IsNullOrWhiteSpace(body.NominalDiam) || string.IsNullOrWhiteSpace(body.Grade)
                || string.IsNullOrWhiteSpace(body.MaterialCode))
                return Results.BadRequest(new { error = "name / nominalDiam / grade / materialCode 必填" });
            if (body.EffectiveLengthMm is not > 0 || body.TotalLengthMm is not > 0)
                return Results.BadRequest(new { error = "有效长度 / 总长 必填且为正数" });

            // 覆盖字段缺省时，国标代码表必须能解析出值——否则这个预设建档时会缺 As/Rp0.2
            var diamMm = BoltStandards.ParseNominalDiamMm(body.NominalDiam);
            if (diamMm is null)
                return Results.BadRequest(new { error = $"无法解析公称直径：{body.NominalDiam}（形如 M20）" });
            if (body.CrossSectionMm2 is null && BoltStandards.StressAreaMm2(diamMm.Value) is null)
                return Results.BadRequest(new { error = $"{body.NominalDiam} 不在 GB/T 16823.1 粗牙表内，请显式提供 crossSectionMm2" });
            if (body.Rp02Mpa is null && BoltStandards.Rp02Mpa(body.Grade, diamMm.Value) is null)
                return Results.BadRequest(new { error = $"等级 {body.Grade} 不在 GB/T 3098.1 表内，请显式提供 rp02Mpa" });

            try
            {
                await using var conn = await ds.OpenConnectionAsync(ct);
                await using var tx = await conn.BeginTransactionAsync(ct);
                long id;
                await using (var cmd = new NpgsqlCommand(@"
                    INSERT INTO bolt_spec_presets
                        (name, nominal_diam, grade, material_code, cross_section_mm2, rp02_mpa,
                         effective_length_mm, total_length_mm, target_preload_n, min_preload_n, note)
                    VALUES (@name, @nd, @grade, @mat, @as, @rp, @el, @tl, @target, @min, @note)
                    RETURNING id", conn, tx))
                {
                    cmd.Parameters.AddWithValue("name", NpgsqlDbType.Varchar, body.Name.Trim());
                    cmd.Parameters.AddWithValue("nd", NpgsqlDbType.Varchar, body.NominalDiam.Trim().ToUpperInvariant());
                    cmd.Parameters.AddWithValue("grade", NpgsqlDbType.Varchar, body.Grade.Trim());
                    cmd.Parameters.AddWithValue("mat", NpgsqlDbType.Varchar, body.MaterialCode.Trim());
                    cmd.Parameters.AddWithValue("as", NpgsqlDbType.Numeric, (object?)body.CrossSectionMm2 ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("rp", NpgsqlDbType.Numeric, (object?)body.Rp02Mpa ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("el", NpgsqlDbType.Numeric, body.EffectiveLengthMm.Value);
                    cmd.Parameters.AddWithValue("tl", NpgsqlDbType.Numeric, body.TotalLengthMm.Value);
                    cmd.Parameters.AddWithValue("target", NpgsqlDbType.Numeric, (object?)body.TargetPreloadN ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("min", NpgsqlDbType.Numeric, (object?)body.MinPreloadN ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("note", NpgsqlDbType.Text, (object?)body.Note ?? DBNull.Value);
                    id = (long)(await cmd.ExecuteScalarAsync(ct) ?? 0L);
                }
                await AuditAsync(conn, tx, user.UserId, "bolt_preset_create", id,
                    $"新建规格预设「{body.Name}」：{body.NominalDiam} {body.Grade} {body.MaterialCode}", ct);
                await tx.CommitAsync(ct);
                return Results.Ok(new { id, name = body.Name.Trim() });
            }
            catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
            {
                return Results.Conflict(new { error = $"预设名已存在：{body.Name}" });
            }
            catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.ForeignKeyViolation)
            {
                return Results.BadRequest(new { error = $"材料不存在：{body.MaterialCode}" });
            }
        })
        .RequireAuthorization(AuthPolicies.ReviewerAccess)
        .WithName("CreateBoltSpecPreset")
        .WithSummary("新建规格预设（reviewer+，审计）");

        grp.MapPatch("/presets/{id:long}", async (
            long id, BoltSpecPresetRequest? body, HttpContext http, NpgsqlDataSource ds, CancellationToken ct) =>
        {
            var user = UserContext.Require(http);
            if (body is null) return Results.BadRequest(new { error = "请求体为空" });

            await using var conn = await ds.OpenConnectionAsync(ct);
            await using var tx = await conn.BeginTransactionAsync(ct);
            string? name = null;
            try
            {
                await using var cmd = new NpgsqlCommand(@"
                    UPDATE bolt_spec_presets SET
                        name = COALESCE(@name, name),
                        nominal_diam = COALESCE(@nd, nominal_diam),
                        grade = COALESCE(@grade, grade),
                        material_code = COALESCE(@mat, material_code),
                        cross_section_mm2 = COALESCE(@as, cross_section_mm2),
                        rp02_mpa = COALESCE(@rp, rp02_mpa),
                        effective_length_mm = COALESCE(@el, effective_length_mm),
                        total_length_mm = COALESCE(@tl, total_length_mm),
                        target_preload_n = COALESCE(@target, target_preload_n),
                        min_preload_n = COALESCE(@min, min_preload_n),
                        note = COALESCE(@note, note),
                        enabled = COALESCE(@enabled, enabled),
                        updated_at = now()
                    WHERE id = @id
                    RETURNING name", conn, tx);
                cmd.Parameters.AddWithValue("id", NpgsqlDbType.Bigint, id);
                cmd.Parameters.AddWithValue("name", NpgsqlDbType.Varchar, (object?)body.Name ?? DBNull.Value);
                cmd.Parameters.AddWithValue("nd", NpgsqlDbType.Varchar, (object?)body.NominalDiam ?? DBNull.Value);
                cmd.Parameters.AddWithValue("grade", NpgsqlDbType.Varchar, (object?)body.Grade ?? DBNull.Value);
                cmd.Parameters.AddWithValue("mat", NpgsqlDbType.Varchar, (object?)body.MaterialCode ?? DBNull.Value);
                cmd.Parameters.AddWithValue("as", NpgsqlDbType.Numeric, (object?)body.CrossSectionMm2 ?? DBNull.Value);
                cmd.Parameters.AddWithValue("rp", NpgsqlDbType.Numeric, (object?)body.Rp02Mpa ?? DBNull.Value);
                cmd.Parameters.AddWithValue("el", NpgsqlDbType.Numeric, (object?)body.EffectiveLengthMm ?? DBNull.Value);
                cmd.Parameters.AddWithValue("tl", NpgsqlDbType.Numeric, (object?)body.TotalLengthMm ?? DBNull.Value);
                cmd.Parameters.AddWithValue("target", NpgsqlDbType.Numeric, (object?)body.TargetPreloadN ?? DBNull.Value);
                cmd.Parameters.AddWithValue("min", NpgsqlDbType.Numeric, (object?)body.MinPreloadN ?? DBNull.Value);
                cmd.Parameters.AddWithValue("note", NpgsqlDbType.Text, (object?)body.Note ?? DBNull.Value);
                cmd.Parameters.AddWithValue("enabled", NpgsqlDbType.Boolean, (object?)body.Enabled ?? DBNull.Value);
                name = await cmd.ExecuteScalarAsync(ct) as string;
            }
            catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.ForeignKeyViolation)
            {
                await tx.RollbackAsync(ct);
                return Results.BadRequest(new { error = $"材料不存在：{body.MaterialCode}" });
            }
            if (name is null) { await tx.RollbackAsync(ct); return Results.NotFound(new { error = "预设不存在", id }); }
            await AuditAsync(conn, tx, user.UserId, "bolt_preset_update", id, $"修改规格预设「{name}」", ct);
            await tx.CommitAsync(ct);
            return Results.Ok(new { id, name, updated = true });
        })
        .RequireAuthorization(AuthPolicies.ReviewerAccess)
        .WithName("UpdateBoltSpecPreset")
        .WithSummary("修改/停用规格预设（reviewer+，审计）");

        // ---- 展开器：建档/改规格/导入共用的唯一推导入口 ----
        grp.MapGet("/resolve", async (
            string? nominalDiam, string? grade, string? materialCode,
            NpgsqlDataSource ds, CancellationToken ct) =>
        {
            var diamMm = BoltStandards.ParseNominalDiamMm(nominalDiam);
            decimal? stressArea = diamMm is { } d ? BoltStandards.StressAreaMm2(d) : null;
            decimal? rp02 = diamMm is { } d2 && !string.IsNullOrWhiteSpace(grade)
                ? BoltStandards.Rp02Mpa(grade!, d2) : null;

            object? material = null;
            if (!string.IsNullOrWhiteSpace(materialCode))
            {
                await using var cmd = ds.CreateCommand(@"
                    SELECT code, name, sound_velocity_ms, temp_coefficient_per_c,
                           acoustic_elastic_factor, elastic_modulus_gpa
                    FROM material_specs WHERE code = @code AND enabled");
                cmd.Parameters.AddWithValue("code", NpgsqlDbType.Varchar, materialCode!.Trim());
                await using var r = await cmd.ExecuteReaderAsync(ct);
                if (await r.ReadAsync(ct))
                    material = new
                    {
                        code = r.GetString(0), name = r.GetString(1),
                        soundVelocityMs = r.GetDecimal(2), tempCoefficientPerC = r.GetDecimal(3),
                        acousticElasticFactor = r.GetDecimal(4), elasticModulusGpa = r.GetDecimal(5),
                    };
            }

            return Results.Ok(new
            {
                nominalDiamMm = diamMm,
                stressAreaMm2 = stressArea,   // null = 不在粗牙表内，需手填
                rp02Mpa = rp02,               // null = 等级未收录，需手填
                material,                     // null = 材料不存在/已停用
            });
        })
        .RequireAuthorization(AuthPolicies.LoggedIn)
        .WithName("ResolveBoltSpec")
        .WithSummary("参数展开：规格+等级查国标代码表、材料查库，一次返回全部推导值");

        return app;
    }

    private static async Task AuditAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx, long userId, string action, long targetId, string note, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(@"
            INSERT INTO audit_log (user_id, action, target_type, target_id, note, at, ip_addr)
            VALUES (@uid, @action, 'spec_library', @tid, @note, now(), @ip)", conn, tx);
        cmd.Parameters.AddWithValue("uid", NpgsqlDbType.Bigint, userId);
        cmd.Parameters.AddWithValue("action", NpgsqlDbType.Varchar, action);
        cmd.Parameters.AddWithValue("tid", NpgsqlDbType.Bigint, targetId);
        cmd.Parameters.AddWithValue("note", NpgsqlDbType.Text, note);
        AuditClientIp.Bind(cmd.Parameters);
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
