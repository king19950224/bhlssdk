using System.Collections.Concurrent;
using BoltPreload.Algorithms.Preload;
using BoltPreload.Algorithms.Waveform;
using BoltPreload.Application.Alarm;
using BoltPreload.Application.Ingestion;
using BoltPreload.Application.Realtime;
using BoltPreload.Fx.Models;
using Microsoft.Extensions.Logging;
using Npgsql;
using NpgsqlTypes;

namespace BoltPreload.Infrastructure.Ingestion;

/// <summary>
/// 时序数据写入器（直接走 Npgsql，绕开 EF Core 的 change tracking）。
/// <para>Phase 1 的简化实现：单条 INSERT。Phase 2+ 改批量 COPY 提性能（详见方案 §5.3）。</para>
/// </summary>
public sealed class PostgresMeasurementSink : IMeasurementSink
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly ILogger<PostgresMeasurementSink> _logger;
    private readonly IChannelResolver _resolver;
    private readonly IWaveStore? _waveStore;
    private readonly IAlarmEvaluator? _alarmEvaluator;
    private readonly IAlarmSink? _alarmSink;
    private readonly IAlarmPublisher? _alarmPublisher;
    private readonly IPreloadCalculatorFactory _calculatorFactory;

    private static readonly System.Text.Json.JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    // 未路由通道（未接探头 / 已软删的"墓碑"通道，按设计不自动复活）的丢弃日志：按【设备】聚合 + 节流。
    // 真机常有几十路空通道持续上报，若逐通道 WARN，开机即刷几十条、之后每 5 分钟再刷几十条，淹没业务日志。
    // 改为：每设备首次出现立即记一条提示，之后每 UnroutedLogInterval 汇总成一条（列出全部受影响通道 + 自启动累计丢弃数）。
    private readonly ConcurrentDictionary<long, UnroutedDeviceState> _unroutedByDevice = new();
    private static readonly TimeSpan UnroutedLogInterval = TimeSpan.FromMinutes(5);

    // wave_features 写失败日志节流（按 channelId）：首条带完整异常含堆栈便于定位根因；
    // 之后同一通道持续失败仅每 WaveFeatureFailLogInterval 记一条摘要（不重复整段堆栈），既保留可观测性又不刷屏。
    private readonly ConcurrentDictionary<long, (DateTimeOffset LastLog, long Count)> _waveFailThrottle = new();
    private static readonly TimeSpan WaveFeatureFailLogInterval = TimeSpan.FromMinutes(1);

    /// <summary>单设备的未路由通道丢弃聚合状态（每通道累计计数 + 节流时间戳）。</summary>
    private sealed class UnroutedDeviceState
    {
        public long FirstNoticeLogged;   // 0=未记首条提示，1=已记（Interlocked CAS）
        public long LastSummaryTicks;     // 上次汇总时间 UtcTicks（Interlocked）
        public readonly ConcurrentDictionary<ushort, long> DropCounts = new();  // 每通道自启动累计丢弃数
    }

    public PostgresMeasurementSink(
        NpgsqlDataSource dataSource,
        IChannelResolver resolver,
        ILogger<PostgresMeasurementSink> logger,
        IWaveStore? waveStore = null,
        IAlarmEvaluator? alarmEvaluator = null,
        IAlarmSink? alarmSink = null,
        IAlarmPublisher? alarmPublisher = null,
        IPreloadCalculatorFactory? calculatorFactory = null)
    {
        _dataSource = dataSource;
        _resolver = resolver;
        _logger = logger;
        _waveStore = waveStore;
        _alarmEvaluator = alarmEvaluator;
        _alarmSink = alarmSink;
        _alarmPublisher = alarmPublisher;
        // 未注入工厂时（如单测直接 new）退化为默认工厂，保持原单波法行为
        _calculatorFactory = calculatorFactory ?? new PreloadCalculatorFactory(
            new SimplifiedCalculator(), new GbSingleWaveCalculator(), new GbBiWaveCalculator());
        _logger.LogInformation("PostgresMeasurementSink 启动，WaveStore={Wave} Alarm={Alarm}",
            waveStore is not null ? "已注入" : "未注入",
            alarmEvaluator is not null ? "已启用" : "未启用");
    }

    /// <summary>
    /// 按 envelope 类型路由：
    /// <list type="bullet">
    /// <item>ChannelResult → measurements / measurement_anomalies</item>
    /// <item>DeviceParameter → device_parameter_history（诊断/审计用）</item>
    /// <item>MultiChannelResult → 拆开按 ChannelResult 写</item>
    /// </list>
    /// </summary>
    public Task WriteAsync(FxDecodedEnvelope envelope, CancellationToken cancellationToken = default)
        => WriteAsync(envelope, evaluateAlarms: true, cancellationToken);

    public async Task WriteAsync(FxDecodedEnvelope envelope, bool evaluateAlarms, CancellationToken cancellationToken = default)
    {
        var ct = cancellationToken;

        if (envelope.ResultType == FxResultType.DeviceParameter && envelope.DeviceParameter is not null)
        {
            await WriteDeviceParameterAsync(envelope, envelope.DeviceParameter, ct);
            return;
        }

        if (envelope.ResultType == FxResultType.MultiChannelResult && envelope.MultiChannelResult is not null)
        {
            // 拆开多通道为单通道结果分别入库
            foreach (var (chNo, chResult) in envelope.MultiChannelResult.Channels)
            {
                await WriteChannelResultAsync(envelope, chResult, evaluateAlarms, ct);
            }
            return;
        }

        if (envelope.ResultType != FxResultType.ChannelResult || envelope.ChannelResult is null)
            return;

        await WriteChannelResultAsync(envelope, envelope.ChannelResult, evaluateAlarms, ct);
    }

    private async Task WriteChannelResultAsync(FxDecodedEnvelope envelope, FxChannelResultDto cr, bool evaluateAlarms, CancellationToken ct)
    {
        var route = await _resolver.ResolveAsync(envelope.DeviceId, cr.Channel, envelope.ReceiveTime, ct);

        switch (route)
        {
            case { BoltId: > 0, ChannelId: > 0 } r:
                if (cr.StatusCode < 0)
                    await WriteAnomalyAsync((r.BoltId, r.ChannelId), envelope, cr, ct);
                else
                    await WriteMeasurementAsync(r, envelope, cr, evaluateAlarms, ct);
                break;

            case { BoltId: 0, ChannelId: > 0 } r:
                await WriteUnboundAsync(r.ChannelId, envelope, cr, ct);
                break;

            default:
                LogUnroutedThrottled(envelope.DeviceId, cr.Channel);
                break;
        }
    }

    /// <summary>
    /// 未路由通道（未接探头 / 已软删的"墓碑"通道，按设计不自动复活）的丢弃日志：按设备聚合 + 节流。
    /// 每设备首次出现立即记一条提示，之后每 <see cref="UnroutedLogInterval"/> 汇总成一条
    /// （列出全部受影响通道 + 自启动累计丢弃数）。真机几十路空通道持续上报时，避免逐通道 WARN 刷屏。
    /// </summary>
    private void LogUnroutedThrottled(long deviceId, ushort channel)
    {
        var st = _unroutedByDevice.GetOrAdd(
            deviceId,
            _ => new UnroutedDeviceState { LastSummaryTicks = DateTimeOffset.UtcNow.UtcTicks });
        st.DropCounts.AddOrUpdate(channel, 1L, (_, c) => c + 1);

        // 该设备首次出现无法路由通道：立即记一条，避免静默等到首个汇总周期
        if (Interlocked.CompareExchange(ref st.FirstNoticeLogged, 1, 0) == 0)
        {
            _logger.LogWarning(
                "device={DeviceId} 出现无法路由的通道（未接探头 / 已软删的墓碑通道？），数据将被丢弃；" +
                "后续每 {Min} 分钟汇总一次受影响通道清单",
                deviceId, (int)UnroutedLogInterval.TotalMinutes);
            return;
        }

        // 周期汇总：每设备每 UnroutedLogInterval 一条，列出全部受影响通道 + 自启动累计丢弃数
        var now = DateTimeOffset.UtcNow.UtcTicks;
        var last = Interlocked.Read(ref st.LastSummaryTicks);
        if (now - last >= UnroutedLogInterval.Ticks &&
            Interlocked.CompareExchange(ref st.LastSummaryTicks, now, last) == last)
        {
            var snapshot = st.DropCounts.ToArray();
            var channels = string.Join(",", snapshot.Select(p => p.Key).OrderBy(c => c));
            var total = snapshot.Sum(p => p.Value);
            _logger.LogWarning(
                "device={DeviceId} 无法路由通道丢弃汇总：通道=[{Channels}] 自启动累计 {Total} 条" +
                "（这些通道未接探头或为已软删的墓碑通道）",
                deviceId, channels, total);
        }
    }

    /// <summary>
    /// 路由命中已绑定通道时，尝试算 preload_n / preload_ratio。
    /// 算不出（缺标定 / 缺规格 / EchoTime 空 / 超出 GB 适用范围 / 算出负值）就 NULL，
    /// 由 AlgoVersion 字段记录原因，方便后续审计回放。
    /// </summary>
    private (decimal? PreloadN, decimal? PreloadRatio, decimal? UncertaintyN, decimal DeltaTofNs, string AlgoVersion, Guid? CalibrationId)
        TryComputePreload(ChannelRoute route, FxChannelResultDto cr)
    {
        if (cr.EchoTimeNs is null || cr.SpTempC is null)
            return (null, null, null, 0m, "phase1:raw", null);

        if (route.BoltSpec is null)
            return (null, null, null, 0m, "phase1:raw", null);

        // 温度系数：优先用螺栓自带的（按材料标准值），缺则退化到 SDK 默认。
        // SDK 默认温度系数（plan §8.4.5 引）—— 仅用于标定/物料配置缺失时兜底。
        const decimal SdkDefaultTempCoeff = 1.0269e-4m;
        var tempCoeff = route.BoltSpec.TempCoefficientPerC ?? SdkDefaultTempCoeff;

        var boltSpec = new BoltSpec(
            Grade: route.BoltSpec.Grade,
            Rp02Mpa: route.BoltSpec.Rp02Mpa,
            ElasticModulusGpa: route.BoltSpec.ElasticModulusGpa,
            NominalDiamMm: route.BoltSpec.NominalDiamMm,
            EffectiveLengthMm: route.BoltSpec.EffectiveLengthMm,
            CrossSectionMm2: route.BoltSpec.CrossSectionMm2,
            SoundVelocityMs: route.BoltSpec.SoundVelocityMs);

        if (route.Calibration is not null && route.CanComputePreload)
        {
            // 标定过期：不得当作"正式"预紧力监测值（plan §7.3，商用安全底线）。
            // 仍保留原始 echo/温度入库，但 preload_n/ratio 置空、algo_version 标记 calibration_expired，
            // 供审计回放与前端灰显；preload 为空 → 不会据此触发预紧力告警。
            if (route.Calibration.IsExpiredAsOf(DateTime.UtcNow))
                return (null, null, null, cr.DeltaTofNs ?? 0m,
                    $"phase1:calibration_expired:{route.Calibration.CalibrationId}", route.Calibration.CalibrationId);

            // 标定未达合格线（passed=false，例如 torque_fit R² 落在 [0.95,0.98) 临时可用层）：
            // 与过期一样不得当"正式"预紧力监测值；保留原始 echo/温度，preload 置空 + 专门 algo_version 供审计/灰显。
            if (!route.Calibration.Passed)
                return (null, null, null, cr.DeltaTofNs ?? 0m,
                    $"phase1:calibration_not_passed:{route.Calibration.CalibrationId}", route.Calibration.CalibrationId);

            // 多点拟合（如 torque_fit）会带截距 b：Δt = K_L * F + b。
            // 此时必须用 F = (Δt - b) / K_L，不能退化成 F = Δt / K_L。
            if (route.Calibration.InterceptNs.HasValue)
            {
                var deltaTempC = cr.SpTempC.Value - route.Calibration.RefTempC;
                var tempFactor = 1m + tempCoeff * deltaTempC;
                if (tempFactor <= 0m || route.Calibration.KL <= 0m)
                    return (null, null, null, 0m, "phase1:torque_fit:invalid", route.Calibration.CalibrationId);

                var normalizedTofNs = cr.EchoTimeNs.Value / tempFactor;
                var deltaTofNs = normalizedTofNs - route.Calibration.RefTimeNs;
                var preloadN = (deltaTofNs - route.Calibration.InterceptNs.Value) / route.Calibration.KL;

                // torque_fit 算出负值 = 标定方向反或基线漂；不要 Abs 抹平
                if (preloadN < 0m)
                    return (null, null, null, deltaTofNs,
                        "phase1:TorqueFitSingleWave:1.0.0:negative_preload", route.Calibration.CalibrationId);

                // 适用范围（同 GbSingleWave）：30~90% Rp0.2；超范围不出 preload_n（防"算出假预紧力被当真"）
                if (boltSpec.CrossSectionMm2 > 0m)
                {
                    var stressMpa = preloadN / boltSpec.CrossSectionMm2;
                    if (stressMpa < boltSpec.Rp02Mpa * 0.30m || stressMpa > boltSpec.Rp02Mpa * 0.90m)
                        return (null, null, null, deltaTofNs,
                            "phase1:TorqueFitSingleWave:1.0.0:out_of_gb_range", route.Calibration.CalibrationId);
                }

                var uncertaintyN = preloadN * 0.30m; // 扭矩拟合仍按临时工程估算处理
                decimal? torqueFitRatio = null;
                if (route.TargetPreloadN is > 0)
                    torqueFitRatio = preloadN / route.TargetPreloadN.Value;

                return (preloadN, torqueFitRatio, uncertaintyN, deltaTofNs,
                    "phase1:TorqueFitSingleWave:1.0.0", route.Calibration.CalibrationId);
            }

            // 路径 B：GB 单波法（标定后）
            var input = new PreloadInput(
                EchoTimeNs: cr.EchoTimeNs.Value,
                RefTimeNs: route.Calibration.RefTimeNs,
                TempC: cr.SpTempC.Value,
                RefTempC: route.Calibration.RefTempC,
                TempCoefficient: tempCoeff,
                Bolt: boltSpec,
                Calibration: new CalibrationData(
                    CalibrationId: route.Calibration.CalibrationId,
                    KL: route.Calibration.KL,
                    KLUncertainty: route.Calibration.KLUncertainty,
                    CalibratedAt: DateTimeOffset.UtcNow,  // CachedCalibration 未带 calibrated_at；calculator 不依赖此字段
                    // M4：透传真实有效期，使 PreloadCalculatorFactory.Pick 的 IsExpired 基于真实值（消除"恒 false 的死代码"脆弱性）。
                    // 注：sink 在调用前已用 route.Calibration.IsExpiredAsOf 拦截过期标定，这里是防御性一致；
                    //     active_calibration_id 必对应 NOT NULL 的 expires_at，null 仅作理论兜底。
                    ExpiresAt: route.Calibration.ExpiresAt is { } exp
                        ? new DateTimeOffset(exp, TimeSpan.Zero)
                        : DateTimeOffset.UtcNow.AddYears(1),
                    Method: "quick"));

            // 经工厂按螺栓规格/标定状态选择计算器（≥M5 标定齐全 → 单波法；≥M12 且双波法可用 → 双波法）。
            var calc = _calculatorFactory.Pick(boltSpec, input.Calibration, DateTimeOffset.UtcNow);
            var result = calc.Calculate(input);

            // GB 范围外不出 preload_n —— calculator 标 WithinGbRange=false 但仍返回 preload_n，
            // 这里在写库前再过滤一遍，避免下游业务把超范围预紧力当真。
            if (result.PreloadN.HasValue && !result.WithinGbRange)
            {
                return (null, null, null, result.DeltaTofNs,
                    $"phase1:{result.ModelUsed}:out_of_gb_range", result.CalibrationId);
            }

            decimal? ratio = null;
            if (result.PreloadN.HasValue && route.TargetPreloadN is > 0)
                ratio = result.PreloadN.Value / route.TargetPreloadN.Value;

            return (result.PreloadN, ratio, result.UncertaintyN, result.DeltaTofNs,
                $"phase1:{result.ModelUsed}:{calc.Version}", result.CalibrationId);
        }

        // 未标定通道不计算 preload_n，避免把 "0N" 当成真实预紧力。
        // SDK 如果有 ChannelConfig.RefTime/RefTemp，会给 DeltaTofNs；这里保留它。
        return (null, null, null, cr.DeltaTofNs ?? 0m, "phase1:raw", null);
    }

    // 通道解析逻辑搬到 IChannelResolver（CachedChannelResolver 带 IMemoryCache）

    private async Task WriteMeasurementAsync(ChannelRoute route, FxDecodedEnvelope envelope, FxChannelResultDto cr, bool evaluateAlarms, CancellationToken ct)
    {
        // 1. 算预紧力（hot path 调用 calculator，纯算术，极快）
        var pre = TryComputePreload(route, cr);

        // 2. 如果带波形且有 WaveStore，先落文件拿 URL（失败不阻塞主入库）
        string? waveUrl = null;
        if (cr.EchoWave is not null && _waveStore is not null && cr.EchoWave.WaveData.Length > 0)
        {
            try
            {
                waveUrl = await _waveStore.SaveAsync(envelope.DeviceId, cr.Channel, envelope.ReceiveTime, cr.EchoWave, ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "保存 EchoWave 失败 device={DeviceId} ch={Ch}", envelope.DeviceId, cr.Channel);
            }
        }

        // 2b. 测量级信号健康字段（参考无关、热路径廉价计算），不再恒为 NULL：
        //     snr_db = 峰峰 / 噪声段 RMS；main_freq_mhz = FFT 谱峰；health_score = §8.6.4 的 SNR 分项映射。
        //     similarity 需与参考波形做互相关（O(n²)），不适合逐条测量算，留给波形对比诊断路径，这里保持 NULL。
        //     feature_vector（机器学习特征）同样属诊断/离线路径，保持 NULL。
        decimal? snrDb = null, mainFreqMhz = null;
        short? healthScore = null;
        if (cr.EchoWave is not null && cr.EchoWave.WaveData.Length > 0)
        {
            var stats = ComputeWaveStats(cr.EchoWave);
            snrDb = stats.SnrDb;
            var mf = ToDecimalSafe(WaveDiagnostics.MainFrequencyMhz(cr.EchoWave.WaveData, cr.EchoWave.ClockInt));
            mainFreqMhz = mf is { } v ? Math.Clamp(v, 0m, 999.99m) : null;   // numeric(5,2) 量程
            if (stats.SnrDb is { } snr)
                healthScore = (short)WaveDiagnostics.SnrOnlyHealthScore((double)snr);
        }

        // 3. INSERT 一次性带上所有计算结果（preload_n / preload_ratio / uncertainty / delta_tof / algo_version / calibration_id）
        const string sql = @"
            INSERT INTO measurements (
                bolt_id, channel_id, ts, ingested_at,
                echo_time_ns, delta_tof_ns, sp_temp_c, gain, status_code,
                preload_n, preload_ratio, preload_uncertainty_n,
                health_score, snr_db, similarity, main_freq_mhz,
                feature_vector, wave_blob_url, algo_version, calibration_id
            ) VALUES (
                @bolt_id, @channel_id, @ts, @ingested_at,
                @echo_time_ns, @delta_tof_ns, @sp_temp_c, @gain, @status_code,
                @preload_n, @preload_ratio, @preload_unc,
                @health_score, @snr_db, NULL, @main_freq,
                NULL, @wave_url, @algo_version, @calib_id
            )
            ON CONFLICT (channel_id, ts) DO NOTHING";

        await using var cmd = _dataSource.CreateCommand(sql);
        cmd.Parameters.AddWithValue("bolt_id", NpgsqlDbType.Bigint, route.BoltId);
        cmd.Parameters.AddWithValue("channel_id", NpgsqlDbType.Bigint, route.ChannelId);
        cmd.Parameters.AddWithValue("ts", NpgsqlDbType.TimestampTz, envelope.ReceiveTime);
        cmd.Parameters.AddWithValue("ingested_at", NpgsqlDbType.TimestampTz, envelope.IngestedAt);
        cmd.Parameters.AddWithValue("echo_time_ns", NpgsqlDbType.Numeric, (object?)cr.EchoTimeNs ?? DBNull.Value);
        // delta_tof 优先用计算出来的（标定路径），未标定时保留 SDK 给的 DeltaTofNs
        cmd.Parameters.AddWithValue("delta_tof_ns", NpgsqlDbType.Numeric,
            (object?)(pre.CalibrationId.HasValue ? pre.DeltaTofNs : cr.DeltaTofNs) ?? DBNull.Value);
        cmd.Parameters.AddWithValue("sp_temp_c", NpgsqlDbType.Numeric, (object?)cr.SpTempC ?? DBNull.Value);
        cmd.Parameters.AddWithValue("gain", NpgsqlDbType.Real, cr.Gain);
        cmd.Parameters.AddWithValue("status_code", NpgsqlDbType.Smallint, cr.StatusCode);
        cmd.Parameters.AddWithValue("preload_n", NpgsqlDbType.Numeric, (object?)pre.PreloadN ?? DBNull.Value);
        // preload_ratio 列是 numeric(6,4)（±99.9999）。目标值配错或极端过载会让 ratio 远超量程，
        // 直接 INSERT 触发 22003 numeric overflow → 整条测量写失败 → 通道彻底静默（安全监测大忌：
        // 传感器还活着却没数据，且误判“探头脱落”）。这里钳到列量程；preload_n（numeric(12,2)）仍存真值，
        // ratio 仅作派生展示，钳制不丢关键信息。告警评估用未钳的真实 ratio（见下），不受影响。
        var preloadRatioClamped = pre.PreloadRatio is { } prRatio
            ? Math.Clamp(prRatio, -99.9999m, 99.9999m)
            : (decimal?)null;
        if (pre.PreloadRatio is { } rawRatio && rawRatio != preloadRatioClamped)
            _logger.LogWarning("preload_ratio={Raw} 超出列量程已钳制 bolt={Bolt}（多半是 target_preload_n 配置过小）",
                rawRatio, route.BoltId);
        cmd.Parameters.AddWithValue("preload_ratio", NpgsqlDbType.Numeric, (object?)preloadRatioClamped ?? DBNull.Value);
        cmd.Parameters.AddWithValue("preload_unc", NpgsqlDbType.Numeric, (object?)pre.UncertaintyN ?? DBNull.Value);
        cmd.Parameters.AddWithValue("health_score", NpgsqlDbType.Smallint, (object?)healthScore ?? DBNull.Value);
        cmd.Parameters.AddWithValue("snr_db", NpgsqlDbType.Numeric, (object?)snrDb ?? DBNull.Value);
        cmd.Parameters.AddWithValue("main_freq", NpgsqlDbType.Numeric, (object?)mainFreqMhz ?? DBNull.Value);
        cmd.Parameters.AddWithValue("wave_url", NpgsqlDbType.Varchar, (object?)waveUrl ?? DBNull.Value);
        cmd.Parameters.AddWithValue("algo_version", NpgsqlDbType.Varchar, pre.AlgoVersion);
        cmd.Parameters.AddWithValue("calib_id", NpgsqlDbType.Uuid, (object?)pre.CalibrationId ?? DBNull.Value);

        // 4. 写库（一次往返），随后做告警评估
        // 注意：先写 measurement 再评估告警，让"告警时对应的 measurement 已经入库可查"
        var rowsAffected = await cmd.ExecuteNonQueryAsync(ct);
        if (rowsAffected == 0)
        {
            // ON CONFLICT (channel_id, ts) 命中：该测量已存在、本次未插入。此时先前落盘的波形成了孤儿
            // （没有对应本次 measurement），删掉它并跳过 wave_features，避免磁盘垃圾与 wave_features 指向错位。
            if (waveUrl is not null)
            {
                try { await _waveStore!.DeleteAsync(waveUrl, ct); }
                catch (Exception ex) { _logger.LogWarning(ex, "删除冲突孤儿波形失败 url={Url}", waveUrl); }
            }
        }
        else if (waveUrl is not null && cr.EchoWave is not null)
        {
            await WriteWaveFeaturesAsync(route.BoltId, route.ChannelId, envelope, cr.EchoWave!, waveUrl, ct);
        }

        // WAL 重放（evaluateAlarms=false）不喂有状态告警状态机：重放的是历史/乱序数据，
        // 喂状态机会用过期数据误触发或误恢复。但 DB 故障窗口内真实发生的脱落 / 严重越限不能漏报，
        // 故重放路径改走无状态 critical 安全网（C1）补判一次。数据照常补写入库。
        if (evaluateAlarms)
        {
            // 仅在真正写入新行时才推进「有状态」告警状态机：ON CONFLICT DO NOTHING 命中（rowsAffected==0）
            // 说明该 (channel_id, ts) 是重复投递、已在首次入库时评估过；再评估会重复累加 on/off-delay 连续计数，
            // 导致提前触发或提前恢复。重复样本直接跳过评估。
            if (rowsAffected > 0)
                await EvaluateAndDispatchAlarmsAsync(route, envelope, cr, pre, ct);
        }
        else
        {
            // 重放路径走无状态 critical 安全网（内部按 (bolt,type) 去重），重复投递时幂等，无需再按 rowsAffected 判。
            await EvaluateReplayCriticalSafetyNetAsync(route, envelope, cr, pre, ct);
        }
    }

    // 接收 FxEchoWaveDto（而非整条 cr），让随测量上报的波形与主动读回的 LEchoWave 共用同一套特征提取。
    // 返回是否真正插入了一行：ON CONFLICT (channel_id, ts) 命中时返回 false，调用方据此清理孤儿波形文件。
    private async Task<bool> WriteWaveFeaturesAsync(
        long? boltId,
        long channelId,
        FxDecodedEnvelope envelope,
        FxEchoWaveDto wave,
        string waveUrl,
        CancellationToken ct)
    {
        if (wave.WaveData.Length == 0) return false;

        var stats = ComputeWaveStats(wave);
        var pointCount = stats.PointCount;
        var peakPositive = stats.PeakPositive;
        var peakNegative = stats.PeakNegative;
        var peakToPeak = stats.PeakToPeak;
        var mean = stats.Mean;
        var rms = stats.Rms;
        var noiseRms = stats.NoiseRms;
        var snrDb = stats.SnrDb;

        const string sql = @"
            INSERT INTO wave_features (
                bolt_id, channel_id, ts, wave_blob_url, point_count,
                echo_start_ns, clock_int_ns, threshold,
                peak_positive, peak_negative, peak_to_peak,
                mean_value, rms_value, noise_rms, snr_db
            ) VALUES (
                @bolt_id, @channel_id, @ts, @url, @point_count,
                @echo_start, @clock_int, @threshold,
                @peak_pos, @peak_neg, @peak_to_peak,
                @mean, @rms, @noise_rms, @snr
            )
            ON CONFLICT (channel_id, ts) DO NOTHING";

        try
        {
            await using var cmd = _dataSource.CreateCommand(sql);
            cmd.Parameters.AddWithValue("bolt_id", NpgsqlDbType.Bigint, (object?)boltId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("channel_id", NpgsqlDbType.Bigint, channelId);
            cmd.Parameters.AddWithValue("ts", NpgsqlDbType.TimestampTz, envelope.ReceiveTime);
            cmd.Parameters.AddWithValue("url", NpgsqlDbType.Varchar, waveUrl);
            cmd.Parameters.AddWithValue("point_count", NpgsqlDbType.Integer, pointCount);
            cmd.Parameters.AddWithValue("echo_start", NpgsqlDbType.Numeric, (decimal)wave.EchoStart);
            cmd.Parameters.AddWithValue("clock_int", NpgsqlDbType.Integer, (int)wave.ClockInt);
            cmd.Parameters.AddWithValue("threshold", NpgsqlDbType.Smallint, (object?)wave.Threshold ?? DBNull.Value);
            cmd.Parameters.AddWithValue("peak_pos", NpgsqlDbType.Integer, peakPositive);
            cmd.Parameters.AddWithValue("peak_neg", NpgsqlDbType.Integer, peakNegative);
            cmd.Parameters.AddWithValue("peak_to_peak", NpgsqlDbType.Integer, peakToPeak);
            cmd.Parameters.AddWithValue("mean", NpgsqlDbType.Numeric, (object?)ToDecimalSafe(mean) ?? DBNull.Value);
            cmd.Parameters.AddWithValue("rms", NpgsqlDbType.Numeric, (object?)ToDecimalSafe(rms) ?? DBNull.Value);
            cmd.Parameters.AddWithValue("noise_rms", NpgsqlDbType.Numeric, (object?)ToDecimalSafe(noiseRms ?? 0) ?? DBNull.Value);
            cmd.Parameters.AddWithValue("snr", NpgsqlDbType.Numeric, (object?)snrDb ?? DBNull.Value);
            var rows = await cmd.ExecuteNonQueryAsync(ct);
            return rows > 0;  // 0 = ON CONFLICT 命中（同 channel+ts 已有特征）
        }
        catch (Exception ex)
        {
            // 提为 Warning：measurement 已带 wave_blob_url，但 wave_features 缺失会让 /compare 与"设为参考"
            // 找不到当前特征、A-Scan 不显示却无报错——属于需要被运维看见的静默劣化，不能埋在 Debug 里。
            // 但持续失败（如约束缺失 / DB 故障）会逐条刷整段堆栈淹没日志，故按 channelId 节流：首条带堆栈，后续只记摘要。
            LogWaveFeatureFailureThrottled(channelId, ex);
            return false;
        }
    }

    /// <summary>
    /// wave_features 写失败日志节流（按 channelId）：首条带完整异常（含堆栈）便于定位根因；
    /// 之后同一通道持续失败仅每 <see cref="WaveFeatureFailLogInterval"/> 记一条摘要（不重复整段堆栈）。
    /// </summary>
    private void LogWaveFeatureFailureThrottled(long channelId, Exception ex)
    {
        var detail = ex is PostgresException pg ? $"{pg.SqlState} {pg.MessageText}" : ex.Message;
        var now = DateTimeOffset.UtcNow;
        var isNew = false;
        _waveFailThrottle.AddOrUpdate(
            channelId,
            _ => { isNew = true; return (now, 1L); },
            (_, prev) => (prev.LastLog, prev.Count + 1));

        if (isNew)
        {
            // 首条：带完整异常（含堆栈），便于定位根因（如约束缺失 / 类型不匹配 / DB 故障）
            _logger.LogWarning(ex, "写入 wave_features 失败 channel_id={ChannelId}：{Detail}", channelId, detail);
            return;
        }

        var cur = _waveFailThrottle[channelId];
        if (now - cur.LastLog >= WaveFeatureFailLogInterval &&
            _waveFailThrottle.TryUpdate(channelId, (now, 0L), cur))
        {
            // 后续：只记摘要（不重复整段堆栈），保留可观测性
            _logger.LogWarning(
                "写入 wave_features 持续失败 channel_id={ChannelId}（近 {Min} 分钟≈{Count} 次）：{Detail}",
                channelId, (int)WaveFeatureFailLogInterval.TotalMinutes, cur.Count, detail);
        }
    }

    /// <summary>单条波形的基础统计量（单遍历计算），供 measurements 健康字段与 wave_features 共用。</summary>
    private readonly record struct WaveStats(
        int PointCount, int PeakPositive, int PeakNegative, int PeakToPeak,
        double Mean, double Rms, double? NoiseRms, decimal? SnrDb);

    /// <summary>
    /// 单遍历算 min/max/sum/sumSq + 噪声段 RMS → 峰峰值/RMS/SNR。
    /// 提取为公共方法：measurements 的 snr_db/health_score 与 wave_features 复用同一套，避免重复实现与口径不一致。
    /// </summary>
    private static WaveStats ComputeWaveStats(FxEchoWaveDto wave)
    {
        var data = wave.WaveData;
        var pointCount = data.Length;
        var noiseSamples = Math.Min(Math.Max(30, pointCount / 10), pointCount);

        int peakPositive = short.MinValue, peakNegative = short.MaxValue;
        long sum = 0, noiseSum = 0;
        double sumSq = 0;
        long noiseSumSq = 0;
        for (int i = 0; i < pointCount; i++)
        {
            int v = data[i];
            if (v > peakPositive) peakPositive = v;
            if (v < peakNegative) peakNegative = v;
            sum += v;
            sumSq += (double)v * v;
            if (i < noiseSamples)
            {
                noiseSum += v;
                noiseSumSq += (long)v * v;
            }
        }
        var peakToPeak = pointCount == 0 ? 0 : peakPositive - peakNegative;
        var mean = pointCount > 0 ? (double)sum / pointCount : 0;
        // Var(X) = E[X²] - E[X]²；可能因浮点误差为负的极小数 → Clamp 到 0
        var variance = pointCount > 0 ? Math.Max(0, sumSq / pointCount - mean * mean) : 0;
        var rms = Math.Sqrt(variance);

        double? noiseRms = null;
        if (noiseSamples > 0)
        {
            var noiseMean = (double)noiseSum / noiseSamples;
            var noiseVar = Math.Max(0, (double)noiseSumSq / noiseSamples - noiseMean * noiseMean);
            noiseRms = Math.Sqrt(noiseVar);
        }

        decimal? snrDb = (noiseRms is null || noiseRms <= 0)
            ? null
            : ToDecimalSafe(20.0 * Math.Log10(Math.Max(1.0, peakToPeak / 2.0) / noiseRms.Value));

        return new WaveStats(pointCount, peakPositive, peakNegative, peakToPeak, mean, rms, noiseRms, snrDb);
    }

    /// <summary>
    /// 安全把 double 转 decimal：NaN / Infinity / 超 decimal 范围一律返回 null，
    /// 避免极端波形数据导致 OverflowException 把整条入库链路弄崩。
    /// </summary>
    private static decimal? ToDecimalSafe(double v)
    {
        if (double.IsNaN(v) || double.IsInfinity(v)) return null;
        const double max = 7.9e28;  // decimal.MaxValue 约 7.9e28
        if (v > max || v < -max) return null;
        try { return (decimal)v; }
        catch (OverflowException) { return null; }
    }

    private async Task EvaluateAndDispatchAlarmsAsync(
        ChannelRoute route, FxDecodedEnvelope envelope, FxChannelResultDto cr,
        (decimal? PreloadN, decimal? PreloadRatio, decimal? UncertaintyN, decimal DeltaTofNs, string AlgoVersion, Guid? CalibrationId) pre,
        CancellationToken ct)
    {
        if (_alarmEvaluator is null) return;

        var input = new AlarmInput(
            BoltId: route.BoltId,
            ChannelId: route.ChannelId,
            Ts: envelope.ReceiveTime,
            PreloadN: pre.PreloadN,
            PreloadRatio: pre.PreloadRatio,
            TargetPreloadN: route.TargetPreloadN,
            SdkStatusCode: cr.StatusCode);

        var events = _alarmEvaluator.Evaluate(input);
        if (events.Count == 0) return;

        foreach (var ev in events)
        {
            // 维护抑制(shelve)：命中未取消、未过期的 (bolt,type) 抑制时，跳过触发的落库/推送，
            // 并回滚状态机转移——使抑制到期后同向样本仍能重新触发，不会因内存状态已 active 而永久静默。
            if (ev.Action == AlarmAction.Trigger && await IsAlarmShelvedAsync(ev.BoltId, ev.Type, ct))
            {
                _alarmEvaluator.RollbackTransition(ev);
                _logger.LogInformation("告警被维护抑制(shelve)拦截 bolt={Bolt} type={Type}", ev.BoltId, ev.Type);
                continue;
            }

            var persisted = true;
            if (_alarmSink is not null)
            {
                try
                {
                    await _alarmSink.PersistAsync(ev, ct);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    // 持久化失败：回滚状态机，让后续测量能重新触发/恢复——杜绝"状态机已 active 但 DB
                    // 无对应告警行"导致的永久漏报（C2）。宁可后续重复，绝不漏报。
                    persisted = false;
                    _logger.LogError(ex, "持久化告警失败，已回滚告警状态机以便后续测量重试 bolt={Bolt} type={Type}", ev.BoltId, ev.Type);
                    _alarmEvaluator.RollbackTransition(ev);
                }
            }
            // 仅在成功落库后推送，避免把未持久化的告警推给前端（与 measurement 推送策略一致）。
            if (persisted && _alarmPublisher is not null)
            {
                _ = SafePublishAlarmAsync(ev, ct);
            }
        }
    }

    // 是否存在命中该 (bolt,type) 的生效抑制（未取消且未过期）。bolt_id / alarm_type 为 NULL 表示通配。
    private async Task<bool> IsAlarmShelvedAsync(long boltId, string alarmType, CancellationToken ct)
    {
        await using var cmd = _dataSource.CreateCommand(@"
            SELECT 1 FROM alarm_shelvings
            WHERE cancelled_at IS NULL AND expires_at > now()
              AND (bolt_id IS NULL OR bolt_id = @bolt)
              AND (alarm_type IS NULL OR alarm_type = @type)
            LIMIT 1");
        cmd.Parameters.AddWithValue("bolt", NpgsqlDbType.Bigint, boltId);
        cmd.Parameters.AddWithValue("type", NpgsqlDbType.Varchar, alarmType);
        return await cmd.ExecuteScalarAsync(ct) is not null;
    }

    /// <summary>
    /// WAL 重放兜底（C1）：重放数据不喂有状态状态机（避免乱序误触发 / 误恢复），但若样本处于
    /// "立即危险"的 critical 档（脱落 / 严重越限），仍补发一条<b>去重</b>的 critical 告警，
    /// 否则 DB 故障窗口内真实发生的预紧力失效会永久漏报。
    /// </summary>
    private async Task EvaluateReplayCriticalSafetyNetAsync(
        ChannelRoute route, FxDecodedEnvelope envelope, FxChannelResultDto cr,
        (decimal? PreloadN, decimal? PreloadRatio, decimal? UncertaintyN, decimal DeltaTofNs, string AlgoVersion, Guid? CalibrationId) pre,
        CancellationToken ct)
    {
        if (_alarmEvaluator is null || _alarmSink is null) return;

        var input = new AlarmInput(
            BoltId: route.BoltId,
            ChannelId: route.ChannelId,
            Ts: envelope.ReceiveTime,
            PreloadN: pre.PreloadN,
            PreloadRatio: pre.PreloadRatio,
            TargetPreloadN: route.TargetPreloadN,
            SdkStatusCode: cr.StatusCode);

        var critical = _alarmEvaluator.EvaluateCriticalStateless(input);
        if (critical is null) return;

        try
        {
            var inserted = await _alarmSink.PersistCriticalIfAbsentAsync(critical, ct);
            if (inserted && _alarmPublisher is not null)
                _ = SafePublishAlarmAsync(critical, ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogError(ex, "WAL 重放兜底补发 critical 告警失败 bolt={Bolt} type={Type}", critical.BoltId, critical.Type);
        }
    }

    private async Task SafePublishAlarmAsync(AlarmEvent ev, CancellationToken ct)
    {
        try { await _alarmPublisher!.PublishAsync(ev, ct); }
        catch (OperationCanceledException) { /* 进程退出 */ }
        catch (Exception ex) { _logger.LogDebug(ex, "实时推送告警失败 bolt={Bolt} type={Type}", ev.BoltId, ev.Type); }
    }

    private async Task WriteAnomalyAsync((long BoltId, long ChannelId) ids, FxDecodedEnvelope envelope, FxChannelResultDto cr, CancellationToken ct)
    {
        const string sql = @"
            INSERT INTO measurement_anomalies (bolt_id, channel_id, ts, status_code, reason, raw_data_jsonb)
            VALUES (@bolt_id, @channel_id, @ts, @status_code, @reason, @raw::jsonb)
            ON CONFLICT (bolt_id, ts, status_code) DO NOTHING";

        var reason = cr.StatusCode switch
        {
            -1 => "信号过小",
            -2 => "阈值异常",
            -3 => "信号溢出",
            -4 => "相似度低",
            -5 => "未知异常",
            -6 => "信噪比低",
            _ => $"未知状态 {cr.StatusCode}"
        };

        var rawJson = System.Text.Json.JsonSerializer.Serialize(new
        {
            cr.Address, cr.Channel, cr.StatusCode, cr.EchoTimeNs, cr.SpTempC, cr.Gain
        });

        await using var cmd = _dataSource.CreateCommand(sql);
        cmd.Parameters.AddWithValue("bolt_id", NpgsqlDbType.Bigint, ids.BoltId);
        cmd.Parameters.AddWithValue("channel_id", NpgsqlDbType.Bigint, ids.ChannelId);
        cmd.Parameters.AddWithValue("ts", NpgsqlDbType.TimestampTz, envelope.ReceiveTime);
        cmd.Parameters.AddWithValue("status_code", NpgsqlDbType.Smallint, cr.StatusCode);
        cmd.Parameters.AddWithValue("reason", NpgsqlDbType.Varchar, reason);
        cmd.Parameters.AddWithValue("raw", NpgsqlDbType.Jsonb, rawJson);

        await cmd.ExecuteNonQueryAsync(ct);
    }

    private async Task WriteUnboundAsync(long channelId, FxDecodedEnvelope envelope, FxChannelResultDto cr, CancellationToken ct)
    {
        string? waveUrl = null;
        if (cr.EchoWave is not null && _waveStore is not null && cr.EchoWave.WaveData.Length > 0)
        {
            try
            {
                waveUrl = await _waveStore.SaveAsync(envelope.DeviceId, cr.Channel, envelope.ReceiveTime, cr.EchoWave, ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "保存未绑定 EchoWave 失败 device={DeviceId} ch={Ch}", envelope.DeviceId, cr.Channel);
            }
        }

        // 保留完整 cr（不含 EchoWave 大波形——单独走 EchoWave 持久化通道；这里只存元数据）
        var rawJson = System.Text.Json.JsonSerializer.Serialize(new
        {
            cr.Address, cr.Channel, cr.StatusCode,
            cr.EchoTimeNs, cr.EchoTime2Ns, cr.SpTempC, cr.DeltaTofNs, cr.Gain,
            cr.LowAi, cr.HighAi, cr.Angle
        }, JsonOpts);

        // ON CONFLICT (channel_id, ts) DO NOTHING：与 measurements 热路径同口径，保证 WAL 重放幂等
        //（0022 迁移加了 ux_unbound_channel_ts 唯一索引）。rowsAffected==0 表示已存在，用于清理孤儿波形。
        const string sql = @"
            INSERT INTO unbound_measurements (
                device_id, channel_id, channel_no, ts, ingested_at,
                echo_time_ns, delta_tof_ns, sp_temp_c, gain, status_code, wave_blob_url, raw_payload
            ) VALUES (
                @device_id, @channel_id, @channel_no, @ts, @ingested_at,
                @echo_time_ns, @delta_tof_ns, @sp_temp_c, @gain, @status_code, @wave_url, @raw::jsonb
            )
            ON CONFLICT (channel_id, ts) DO NOTHING";

        await using var cmd = _dataSource.CreateCommand(sql);
        cmd.Parameters.AddWithValue("device_id", NpgsqlDbType.Bigint, envelope.DeviceId);
        cmd.Parameters.AddWithValue("channel_id", NpgsqlDbType.Bigint, channelId);
        cmd.Parameters.AddWithValue("channel_no", NpgsqlDbType.Smallint, (short)cr.Channel);
        cmd.Parameters.AddWithValue("ts", NpgsqlDbType.TimestampTz, envelope.ReceiveTime);
        cmd.Parameters.AddWithValue("ingested_at", NpgsqlDbType.TimestampTz, envelope.IngestedAt);
        cmd.Parameters.AddWithValue("echo_time_ns", NpgsqlDbType.Numeric, (object?)cr.EchoTimeNs ?? DBNull.Value);
        cmd.Parameters.AddWithValue("delta_tof_ns", NpgsqlDbType.Numeric, (object?)cr.DeltaTofNs ?? DBNull.Value);
        cmd.Parameters.AddWithValue("sp_temp_c", NpgsqlDbType.Numeric, (object?)cr.SpTempC ?? DBNull.Value);
        cmd.Parameters.AddWithValue("gain", NpgsqlDbType.Real, cr.Gain);
        cmd.Parameters.AddWithValue("status_code", NpgsqlDbType.Smallint, cr.StatusCode);
        cmd.Parameters.AddWithValue("wave_url", NpgsqlDbType.Varchar, (object?)waveUrl ?? DBNull.Value);
        cmd.Parameters.AddWithValue("raw", NpgsqlDbType.Jsonb, rawJson);

        var rowsAffected = await cmd.ExecuteNonQueryAsync(ct);
        if (rowsAffected == 0)
        {
            // ON CONFLICT (channel_id, ts) 命中（重复投递 / WAL 重放）：本次未插入，先前落盘的波形成孤儿，删掉。
            if (waveUrl is not null)
            {
                try { await _waveStore!.DeleteAsync(waveUrl, ct); }
                catch (Exception ex) { _logger.LogWarning(ex, "删除冲突孤儿未绑定波形失败 url={Url}", waveUrl); }
            }
        }
        else if (waveUrl is not null && cr.EchoWave is not null)
        {
            await WriteWaveFeaturesAsync(null, channelId, envelope, cr.EchoWave!, waveUrl, ct);
        }
    }

    private async Task WriteDeviceParameterAsync(FxDecodedEnvelope envelope, FxDeviceParameterDto dp, CancellationToken ct)
    {
        // 简单类型存 param_value_text；复杂类型（PulseConfig/EchoWave 等对象）存 JSON
        string? valueText = null;
        string? valueJsonb = null;

        switch (dp.ParamValue)
        {
            case null:
                valueText = null;
                break;
            case string s:
                valueText = s;
                break;
            case bool or int or long or short or ushort or uint or ulong or byte or sbyte:
                valueText = dp.ParamValue.ToString();
                break;
            case decimal d:
                valueText = d.ToString(System.Globalization.CultureInfo.InvariantCulture);
                break;
            case double dbl:
                valueText = dbl.ToString(System.Globalization.CultureInfo.InvariantCulture);
                break;
            case float f:
                valueText = f.ToString(System.Globalization.CultureInfo.InvariantCulture);
                break;
            // 主动读回的回波波形（ReadChannelParam("LEchoWave") 响应）：真机常规 MultiMeas 模式不随测量带波形，
            // 靠定期读 LEchoWave 补齐波形诊断数据源。解析通道后落 WaveStore + wave_features（与随测量波形同一套特征提取），
            // 不再塞进 device_parameter_history。守卫不满足时回退到下面 default 的 JSON 存储，不丢数据。
            case FxEchoWaveDto wave when dp.Channel.HasValue && _waveStore is not null && wave.WaveData.Length > 0:
                await PersistActiveEchoWaveAsync(envelope, dp.Channel.Value, wave, ct);
                return;

            default:
                // 复杂对象（PulseConfig、NetCheckConfig 等）序列化成 JSON
                try
                {
                    valueJsonb = System.Text.Json.JsonSerializer.Serialize(dp.ParamValue, JsonOpts);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "DeviceParameter {Param} 值序列化失败，仅存类型名", dp.ParamName);
                    valueText = $"[{dp.ParamValue.GetType().Name}]";
                }
                break;
        }

        const string sql = @"
            INSERT INTO device_parameter_history (
                device_id, address, channel_no, param_name, param_value_text, param_value_jsonb, received_at
            ) VALUES (
                @device_id, @address, @channel_no, @param_name, @value_text, @value_jsonb::jsonb, @received_at
            )";

        await using var cmd = _dataSource.CreateCommand(sql);
        cmd.Parameters.AddWithValue("device_id", NpgsqlDbType.Bigint, envelope.DeviceId);
        cmd.Parameters.AddWithValue("address", NpgsqlDbType.Smallint, (short)dp.Address);
        cmd.Parameters.AddWithValue("channel_no", NpgsqlDbType.Smallint,
            (object?)(dp.Channel.HasValue ? (short)dp.Channel.Value : (object?)null) ?? DBNull.Value);
        cmd.Parameters.AddWithValue("param_name", NpgsqlDbType.Varchar, dp.ParamName);
        cmd.Parameters.AddWithValue("value_text", NpgsqlDbType.Text, (object?)valueText ?? DBNull.Value);
        cmd.Parameters.AddWithValue("value_jsonb", NpgsqlDbType.Jsonb, (object?)valueJsonb ?? DBNull.Value);
        cmd.Parameters.AddWithValue("received_at", NpgsqlDbType.TimestampTz, envelope.ReceiveTime);

        await cmd.ExecuteNonQueryAsync(ct);
    }

    // 把主动读回的 LEchoWave 波形落 WaveStore + wave_features（复用随测量波形的特征提取链路）。
    // 这是"真机常规监测拿不到波形"斷層的入库端修复：让定期读取的波形进入诊断数据源。
    private async Task PersistActiveEchoWaveAsync(FxDecodedEnvelope envelope, ushort channel, FxEchoWaveDto wave, CancellationToken ct)
    {
        var route = await _resolver.ResolveAsync(envelope.DeviceId, channel, envelope.ReceiveTime, ct);
        if (route is null || route.ChannelId <= 0)
        {
            _logger.LogDebug("主动读取的 LEchoWave 通道未路由 device={Dev} ch={Ch}，跳过 wave_features", envelope.DeviceId, channel);
            return;
        }
        try
        {
            var waveUrl = await _waveStore!.SaveAsync(envelope.DeviceId, channel, envelope.ReceiveTime, wave, ct);
            long? boltId = route.BoltId > 0 ? route.BoltId : null;
            var inserted = await WriteWaveFeaturesAsync(boltId, route.ChannelId, envelope, wave, waveUrl, ct);
            if (!inserted)
            {
                // 同 channel+ts 已有 wave_features（重复主动读 / 重放）：刚落盘的文件成了孤儿，删掉避免磁盘垃圾。
                // 注意：随测量上报的波形由 measurement.wave_blob_url 引用，不走此路径，不会被误删。
                try { await _waveStore.DeleteAsync(waveUrl, ct); }
                catch (Exception ex) { _logger.LogWarning(ex, "删除重复 LEchoWave 孤儿波形失败 url={Url}", waveUrl); }
            }
            else
            {
                _logger.LogInformation("主动读取的 LEchoWave 已入 wave_features device={Dev} ch={Ch} pts={Pts}",
                    envelope.DeviceId, channel, wave.WaveData.Length);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "主动读取的 LEchoWave 入库失败 device={Dev} ch={Ch}", envelope.DeviceId, channel);
        }
    }
}
