using BoltPreload.Algorithms.Preload;
using BoltPreload.Api.Auth;
using BoltPreload.Api.Endpoints;
using BoltPreload.Api.Hubs;
using BoltPreload.Api.Runtime;
using BoltPreload.Application.Alarm;
using BoltPreload.Application.Devices;
using BoltPreload.Application.Devices.ProbeScan;
using BoltPreload.Application.Ingestion;
using BoltPreload.Application.Licensing;
using BoltPreload.Application.Realtime;
using BoltPreload.Infrastructure.Alarm;
using BoltPreload.Fx;
using BoltPreload.Fx.Simulator;
using BoltPreload.Infrastructure.Devices;
using BoltPreload.Infrastructure.Devices.ProbeScan;
using BoltPreload.Infrastructure.Ingestion;
using BoltPreload.Infrastructure.Licensing;
using BoltPreload.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using Serilog;

var builder = WebApplication.CreateBuilder(args);
var isDevelopment = builder.Environment.IsDevelopment();

// ============================================================================
// 日志：Serilog 写控制台 + 滚动文件
// 路径用 AppContext.BaseDirectory，防御 Windows 服务 CWD=System32 把日志写错地方
// ============================================================================
var logDir = Path.Combine(AppContext.BaseDirectory, "logs");
Directory.CreateDirectory(logDir);

Log.Logger = new LoggerConfiguration()
    .ReadFrom.Configuration(builder.Configuration)
    .Enrich.FromLogContext()
    .Enrich.WithProperty("Application", "BoltPreload.Api")
    .WriteTo.Console(outputTemplate: "{Timestamp:HH:mm:ss.fff} [{Level:u3}] {Message:lj} {Properties:j}{NewLine}{Exception}")
    .WriteTo.File(
        path: Path.Combine(logDir, "boltpreload-.log"),
        rollingInterval: RollingInterval.Day,
        retainedFileCountLimit: 90,
        outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {SourceContext} {Message:lj}{NewLine}{Exception}")
    .CreateLogger();
builder.Host.UseSerilog();

// ============================================================================
// DI 配置
// ============================================================================

var connectionString = builder.Configuration.GetConnectionString("Postgres");
if (string.IsNullOrWhiteSpace(connectionString))
    throw new InvalidOperationException(
        "缺少 ConnectionStrings:Postgres。请通过环境变量 ConnectionStrings__Postgres、" +
        "appsettings.Development.json 或 dotnet user-secrets 注入。");

// NpgsqlDataSource（共享连接池，整个进程一个）
builder.Services.AddSingleton(_ =>
{
    var dsBuilder = new NpgsqlDataSourceBuilder(connectionString);
    return dsBuilder.Build();
});

// EF Core（业务表 CRUD 的预留入口 + EF 模型↔手写 schema 漂移测试基线）。
// 采集热路径走原生 NpgsqlDataSource（绕开 change tracking）；此 DbContext 当前不在热路径注入，
// 保留它是为了 (1) 后续业务 CRUD 改用 EF；(2) BoltPreload.Tests 用它校验 EF 映射与 deploy/sql schema 不漂移。
// 列名 → snake_case 在 DbContext OnModelCreating 手动 HasColumnName，不用 NamingConventions 包。
builder.Services.AddDbContext<BoltPreloadDbContext>(opt =>
{
    opt.UseNpgsql(connectionString);
});

// 内存缓存（用于 channels / bolts 路由解析）
// SizeLimit 控制条目数上限，防止内存爆掉
builder.Services.AddMemoryCache(options =>
{
    options.SizeLimit = 10_000;  // 单进程支撑 10000 个通道路由条目，远超现实需求
});

// 通道解析（带缓存）
builder.Services.AddSingleton<IChannelResolver, CachedChannelResolver>();

// 预紧力计算器 + 工厂（按螺栓规格 / 标定状态选最高精度可用算法；采集热路径用）。
// 之前 PostgresMeasurementSink 直接 new GbSingleWaveCalculator()，工厂从未接线 → 现改为经工厂注入。
builder.Services.AddSingleton<SimplifiedCalculator>();
builder.Services.AddSingleton<GbSingleWaveCalculator>();
builder.Services.AddSingleton<GbBiWaveCalculator>();
builder.Services.AddSingleton<IPreloadCalculatorFactory, PreloadCalculatorFactory>();

// 时序数据落地
builder.Services.AddSingleton<IMeasurementSink, PostgresMeasurementSink>();

// 测量 WAL 兜底：入库失败落本地盘，DB 恢复后 WalReplayService 重放补写（重放不触发告警）
builder.Services.Configure<MeasurementWalOptions>(builder.Configuration.GetSection(MeasurementWalOptions.SectionName));
if (builder.Configuration.GetValue("MeasurementWal:Enabled", true))
{
    builder.Services.AddSingleton<IMeasurementWal, LocalFileMeasurementWal>();
    builder.Services.AddHostedService<WalReplayService>();
}
else
{
    builder.Services.AddSingleton<IMeasurementWal>(NoopMeasurementWal.Instance);
}

// 波形存储：Phase 1 走本地文件。Phase 3 可换 S3/MinIO 实现 IWaveStore
builder.Services.Configure<LocalFileWaveStoreOptions>(builder.Configuration.GetSection("WaveStore"));
builder.Services.AddSingleton<IWaveStore, LocalFileWaveStore>();

// 开发期间：SDK 协议帧打日志（Phase 2 真机调试用，Phase 3 上线前关掉）。
// 经 BoltPreload.Fx 适配层封装，Api 不直接依赖 FxSDK.Utility。
FxConnectorFactory.DeveloperMode = builder.Configuration.GetValue<bool>("Fx:DeveloperMode", false);

// 多设备运行时管理：从 devices 表加载 N 个设备运行时。
// 注意：不再把任何单例 IFxConnector 注册进 DI —— 业务/诊断服务必须经
// IDeviceConnectorManager.Find(deviceId) 解析对应设备连接器，否则多设备会静默路由到主设备。
builder.Services.AddSingleton<IDeviceConnectorManager, DeviceConnectorManager>();

// 实时推送（SignalR）— 调短 keepalive 让客户端能更快感知断线
builder.Services.AddSignalR(opts =>
{
    opts.KeepAliveInterval = TimeSpan.FromSeconds(10);
    opts.ClientTimeoutInterval = TimeSpan.FromSeconds(30);
});
builder.Services.AddSingleton<IMeasurementPublisher, SignalRMeasurementPublisher>();
builder.Services.AddSingleton<IAlarmPublisher, SignalRAlarmPublisher>();

// 告警评估 + 持久化
builder.Services.Configure<StaticThresholdAlarmOptions>(builder.Configuration.GetSection("Alarm:StaticThreshold"));
builder.Services.AddSingleton<IAlarmEvaluator, StaticThresholdAlarmEvaluator>();
builder.Services.AddSingleton<IAlarmSink, PostgresAlarmSink>();
builder.Services.AddSingleton<IChannelConfigService, PostgresChannelConfigService>();

// 通道自动检测（Probe Scan）
builder.Services.Configure<ProbeScanOptions>(builder.Configuration.GetSection("ProbeScan"));
builder.Services.AddSingleton<ProbeScanState>();
builder.Services.AddSingleton<IProbeScanRepository, PostgresProbeScanRepository>();
builder.Services.AddSingleton<IProbeScanOrchestrator, ProbeScanOrchestrator>();
builder.Services.Configure<LocalFileLicenseOptions>(builder.Configuration.GetSection("License"));
// 把真实环境写入 License 选项：AllowMissingLicenseInDevelopment 只有在 Development 才会被采纳，
// 防止该开关被误配到生产导致无 license 也"有效"。
builder.Services.PostConfigure<LocalFileLicenseOptions>(o => o.IsDevelopmentEnvironment = isDevelopment);
builder.Services.AddSingleton<ILicenseService, LocalFileLicenseService>();
// 授权容量限额的运行时强制状态（启动时按实际设备/通道数评估，LicenseGate 据此拦截写操作）
builder.Services.AddSingleton<LicenseEnforcementState>();

// 鉴权（JWT bearer）+ 授权策略 + 用户密码哈希
builder.Services.AddBoltPreloadAuth(builder.Configuration, isDevelopment);

// 后台服务异常行为：单个 HostedService 抛未捕获异常时【不拖停整个进程】。
// 采集 / 看门狗 / WAL 重放 / 信号健康各自独立，一个崩不应让整机停摆（默认 StopHost 对监测系统太脆）。
builder.Services.Configure<HostOptions>(o =>
    o.BackgroundServiceExceptionBehavior = BackgroundServiceExceptionBehavior.Ignore);

// IngestionService 作为 BackgroundService 跑
builder.Services.AddHostedService<IngestionService>();
builder.Services.Configure<IngestionWatchdogOptions>(builder.Configuration.GetSection("Ingestion:Watchdog"));
builder.Services.AddHostedService<IngestionWatchdogService>();

// 信号健康看门狗：探头脱落 / 接头松动 / 设备掉线导致通道无回波时产生 signal_lost 告警，
// 避免未标定通道的"静默失效"（预紧力告警仅在已标定通道触发）。
builder.Services.Configure<BoltPreload.Api.Runtime.SignalHealthOptions>(builder.Configuration.GetSection("Alarm:SignalHealth"));
builder.Services.AddHostedService<BoltPreload.Api.Runtime.SignalHealthService>();

// 波形采集：真机 MultiMeas 模式不随测量带波形，定期主动读 LEchoWave 补波形诊断数据源（默认关，现场接真机后开）
builder.Services.Configure<BoltPreload.Api.Runtime.WaveSamplingOptions>(builder.Configuration.GetSection("Ingestion:WaveSampling"));
builder.Services.AddHostedService<BoltPreload.Api.Runtime.WaveSamplingService>();

// 仿真模式：为模拟器驱动的螺栓通道写入真实标定，使采集链路算出真实预紧力（不再注入演示数据）。
if (string.Equals(builder.Configuration["Fx:Mode"] ?? "Simulated", "Simulated", StringComparison.OrdinalIgnoreCase))
    builder.Services.AddHostedService<BoltPreload.Api.Runtime.SimulatedCalibrationProvisioner>();

// ============================================================================
// Web API
// ============================================================================
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

// 静态文件（实时演示页 wwwroot/realtime.html）
app.UseDefaultFiles();
app.UseStaticFiles();

// 顺序：Auth → License Gate → 路由
app.UseAuthentication();
app.UseAuthorization();
app.UseMiddleware<LicenseGateMiddleware>();

// SignalR Hub — 必须登录
app.MapHub<MeasurementsHub>("/hubs/measurements").RequireAuthorization(AuthPolicies.LoggedIn);

// 健康检查 + License 状态：始终匿名可访问
app.MapGet("/api/health", () => new
{
    status = "ok",
    timestamp = DateTimeOffset.UtcNow,
    version = typeof(Program).Assembly.GetName().Version?.ToString() ?? "0.0.0"
})
.AllowAnonymous()
.WithTags("Health");

app.MapGet("/api/runtime/devices", (IDeviceConnectorManager manager, ILicenseService licenseService, LicenseEnforcementState enforcement, IMeasurementWal wal) =>
{
    var license = licenseService.GetStatus();
    // 多设备下按 (deviceId, address, channel) 去重，避免不同设备同 address/channel 互相抵消
    var channelCount = manager.Runtimes
        .SelectMany(runtime => runtime.Connector.GetSessionChannelConfigs()
            .Select(c => (runtime.DeviceId, c.Address, c.Channel)))
        .Distinct()
        .Count();

    return Results.Ok(new
    {
        count = manager.Runtimes.Count,
        // WAL 兜底积压：pending>0 表示曾入库失败、正等 DB 恢复后重放补写（运维关注项）
        wal = new { wal.Enabled, pending = wal.PendingCount },
        license = new
        {
            license.Valid,
            license.Found,
            license.LicensedTo,
            license.ExpiresAt,
            license.MaxDevices,
            license.MaxChannels,
            currentDevices = manager.Runtimes.Count,
            currentChannels = channelCount,
            overDeviceLimit = license.MaxDevices.HasValue && manager.Runtimes.Count > license.MaxDevices.Value,
            overChannelLimit = license.MaxChannels.HasValue && channelCount > license.MaxChannels.Value,
            // 强制结果：超限时写操作被 LicenseGate 拦截（监测不停）
            withinLimits = enforcement.WithinLimits,
            capacityViolation = enforcement.Violation,
            license.Warning
        },
        items = manager.Runtimes.Select(runtime => new
        {
            runtime.DeviceId,
            runtime.Name,
            runtime.IsPrimary,
            state = runtime.Connector.State.ToString(),
            runtime.Options,
            lastEnvelopeAt = runtime.RuntimeStatus.LastEnvelopeAt,
            lastChannelResultAt = runtime.RuntimeStatus.LastChannelResultAt,
            runtime.RuntimeStatus.EnvelopeCount,
            runtime.RuntimeStatus.ChannelResultCount,
            runtime.RuntimeStatus.WriteFailureCount,
            sessionChannels = runtime.Connector.GetSessionChannelConfigs()
                .Select(c => new { c.Address, c.Channel, c.ProbeFreqMhz, c.MeasMode, c.Threshold, c.RefTimeNs, c.RefTempC })
                .ToArray()
        })
    });
})
.RequireAuthorization(AuthPolicies.LoggedIn)
.WithTags("Runtime").WithName("ListDeviceRuntimes");

// Auth endpoints（含匿名 /login，内部已配 policy）
app.MapAuthEndpoints();

// 业务 endpoints — 每个内部的 MapGroup 自己已经挂 RequireAuthorization(...)
app.MapMeasurementEndpoints();
app.MapDiagnosticEndpoints();
app.MapChannelConfigChangeEndpoints();
app.MapAuditLogEndpoints();
app.MapAssetEndpoints();
app.MapDashboardEndpoints();
app.MapProbeScanEndpoints();
app.MapChannelEndpoints();
app.MapCalibrationEndpoints();
app.MapBoltEndpoints();
app.MapAlarmEndpoints();
app.MapReportEndpoints();
app.MapLicenseEndpoints();

// 从 DB 重建告警评估器的内存状态机（防止重启后 on-delay 计数清零、已开启告警重复触发）。
// 关键：必须在 app.RunAsync() 之前【同步 await】完成 —— HostedService（IngestionService）的 StartAsync
// 在 RunAsync 内启动，而 ApplicationStarted 事件在 HostedService 启动【之后】才触发；
// 若把 Restore 放进 ApplicationStarted，会与 IngestionService 的告警评估并发，
// 导致重启瞬间对已开启告警重复触发 / 重复写 alarms。
try
{
    var alarmSink = app.Services.GetRequiredService<IAlarmSink>();
    var evaluator = app.Services.GetRequiredService<IAlarmEvaluator>();
    var openAlarms = await alarmSink.LoadOpenAsync();
    if (evaluator is StaticThresholdAlarmEvaluator ste)
        ste.Restore(openAlarms);
}
catch (Exception ex)
{
    // DB 启动时不可达等：记录并继续（此刻 IngestionService 尚未启动、也写不进库，暂不会产生重复告警）。
    Log.Error(ex, "启动重建告警状态机失败（DB 不可达？）；继续启动，但 DB 恢复后的短暂窗口内可能重复触发已开启告警");
}

// 其余【非竞态】的启动收尾（admin 自举、probe scan 残留清理）仍异步进行，不阻塞接客
_ = app.Lifetime.ApplicationStarted.Register(() =>
{
    _ = Task.Run(async () =>
    {
        try
        {
            var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Startup");
            await AdminBootstrap.EnsureDevAdminAsync(app.Services, app.Configuration, isDevelopment, logger, CancellationToken.None);

            // 清理上次崩溃残留的 probe scan 脏记录（status=pending/running）
            var probeScanOpts = app.Services.GetRequiredService<IOptions<ProbeScanOptions>>().Value;
            if (probeScanOpts.CleanStaleRunsOnStartup)
            {
                var probeScanRepo = app.Services.GetRequiredService<IProbeScanRepository>();
                var n = await probeScanRepo.CleanStaleRunsAsync(CancellationToken.None);
                if (n > 0)
                    logger.LogWarning("启动清理 {N} 条残留的 probe scan 记录（标记为 failed）", n);
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "启动 Bootstrap 失败（admin 自举 / probe scan 清理）");
        }
    });
});

// 启动时显著记录授权状态：无效/缺失授权不能悄无声息上线（写操作会被 LicenseGate 拒绝）。
var licenseStatus = app.Services.GetRequiredService<ILicenseService>().GetStatus();
if (licenseStatus.Valid)
    Log.Information("授权有效 LicensedTo={To} 到期={Exp} MaxDevices={MD} MaxChannels={MC}",
        licenseStatus.LicensedTo, licenseStatus.ExpiresAt, licenseStatus.MaxDevices, licenseStatus.MaxChannels);
else
    Log.Warning("授权无效或缺失：{Warning}（机器码 {MachineCode}）。写操作将被拒绝；请安装有效 license.json。",
        licenseStatus.Warning, licenseStatus.MachineCode);

// 容量限额强制：按实际加载的设备 / 通道数评估 MaxDevices / MaxChannels。
// 超限【不停采集】（避免授权问题导致安全监测静默失效），但 LicenseGate 会拦截所有写操作。
try
{
    var manager = app.Services.GetRequiredService<IDeviceConnectorManager>();
    var enforcement = app.Services.GetRequiredService<LicenseEnforcementState>();
    var currentDevices = manager.Runtimes.Count;
    var currentChannels = manager.Runtimes
        .SelectMany(rt => rt.Connector.GetSessionChannelConfigs()
            .Select(c => (rt.DeviceId, c.Address, c.Channel)))
        .Distinct()
        .Count();
    enforcement.Set(currentDevices, currentChannels, licenseStatus.MaxDevices, licenseStatus.MaxChannels);
    if (!enforcement.WithinLimits)
        Log.Error("授权容量超限：{Violation}（当前 设备={Dev}/通道={Ch}，授权 设备={MD}/通道={MC}）。" +
                  "监测继续运行，但所有写操作（配置/标定/下发）将被拒绝，请补足正确授权。",
            enforcement.Violation, currentDevices, currentChannels, licenseStatus.MaxDevices, licenseStatus.MaxChannels);
    else
        Log.Information("授权容量校验通过：设备 {Dev}/{MD}，通道 {Ch}/{MC}",
            currentDevices, licenseStatus.MaxDevices, currentChannels, licenseStatus.MaxChannels);
}
catch (Exception ex)
{
    // 设备运行时构造失败等：记录但不阻塞启动（DeviceConnectorManager 构造异常会在别处 rethrow）。
    Log.Error(ex, "评估授权容量限额失败；写操作闸门将按签名有效性单独判定。");
}

// 启动 — 启动失败一律 rethrow 让进程退出码非 0，systemd / Windows 服务 / docker 才会重启
try
{
    Log.Information("BoltPreload.Api 启动中... Environment={Env}", builder.Environment.EnvironmentName);
    await app.RunAsync();
}
catch (Exception ex)
{
    Log.Fatal(ex, "BoltPreload.Api 启动失败");
    Log.CloseAndFlush();
    throw;
}
finally
{
    Log.CloseAndFlush();
}

// Program 类不密封，方便集成测试用 WebApplicationFactory<Program>
public partial class Program { }
