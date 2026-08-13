using BoltPreload.Application.Diagnostics;
using BoltPreload.Application.Notifications;
using Microsoft.Extensions.Options;
using Npgsql;

namespace BoltPreload.Api.Runtime;

/// <summary>
/// 系统自身故障的外发通知。周期评估系统健康，故障与恢复各推一条，持续未恢复按间隔重提醒。
/// <para>
/// 为什么不复用现成的告警管线：出站通知挂在 <c>notification_outbox</c> 上，而发件箱在库里。
/// 库一挂，告警写不进 <c>alarms</c>、自然也入不了发件箱，“数据库挂了”这件事就永远推不出去。
/// 所以这条路径全程不碰库：渠道地址从 appsettings 读，判定用进程内存状态，
/// 发送直接走 <see cref="INotificationTransport"/>。
/// </para>
/// <para>
/// 与业务告警的分工：螺栓松没松走 alarms 那条线（要留档、要确认、要审计）；
/// 这里只管"系统还能不能信"，不落库、不进告警列表，是一条给值班手机用的旁路。
/// </para>
/// <para>
/// 发送失败只记日志、不更新发送时刻，下一轮自然重试。这里不做退避重试队列——
/// 真要连通知都发不出去，多半是网络整体不通，攒着重试没有意义。
/// </para>
/// </summary>
public sealed class SystemAlertService : BackgroundService
{
    private readonly SystemHealthEvaluator _evaluator;
    private readonly IEnumerable<INotificationTransport> _transports;
    private readonly NpgsqlDataSource _dataSource;
    private readonly BackgroundServiceHealth _health;
    private readonly SystemAlertOptions _options;
    private readonly ILogger<SystemAlertService> _logger;

    private bool _wasUnhealthy;
    private string _lastFingerprint = "";
    private DateTimeOffset _lastSentAt;

    public SystemAlertService(
        SystemHealthEvaluator evaluator,
        IEnumerable<INotificationTransport> transports,
        NpgsqlDataSource dataSource,
        BackgroundServiceHealth health,
        IOptions<SystemAlertOptions> options,
        ILogger<SystemAlertService> logger)
    {
        _evaluator = evaluator;
        _transports = transports;
        _dataSource = dataSource;
        _health = health;
        _options = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled || string.IsNullOrWhiteSpace(_options.WebhookUrl))
        {
            // 不报错也不退出得静悄悄：现场没配就是没配，但生产环境这意味着系统自己出事没人知道，
            // 值得在日志里留一句，别等出了事才发现这条路根本没通。
            _logger.LogWarning(
                "系统告警外发未启用（Notifications:SystemAlert）。系统级故障（库不可达、入库降级、" +
                "缓存满丢数、后台服务失联、磁盘不足）只会体现在 /api/health/ready 与界面横幅上，不会推给任何人。");
            return;
        }

        var transport = _transports.FirstOrDefault(t =>
            string.Equals(t.Kind, _options.Kind, StringComparison.OrdinalIgnoreCase));
        if (transport is null)
        {
            _logger.LogError("系统告警外发配的 Kind={Kind} 没有对应适配器，本服务停止。当前可用：{Kinds}",
                _options.Kind, string.Join(", ", _transports.Select(t => t.Kind)));
            return;
        }

        var interval = TimeSpan.FromSeconds(Math.Max(10, _options.CheckIntervalSeconds));
        var repeat = TimeSpan.FromMinutes(Math.Max(1, _options.RepeatMinutes));
        var graceUntil = DateTimeOffset.UtcNow.AddSeconds(Math.Max(0, _options.StartupGraceSeconds));

        _logger.LogInformation("系统告警外发已启用：每 {Interval} 评估一次，持续故障每 {Repeat} 重提醒一次",
            interval, repeat);

        using var timer = new PeriodicTimer(interval);
        while (!stoppingToken.IsCancellationRequested)
        {
            _health.Beat("system-alert", interval);
            try
            {
                // 启动宽限：进程刚起来时库可能还没就绪、后台服务也还没打第一次心跳，
                // 这段时间的"故障"是假的，报出去只会让人学会忽略这个通知。
                if (DateTimeOffset.UtcNow >= graceUntil)
                    await EvaluateAndNotifyAsync(transport, repeat, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "系统告警评估轮次失败，下一轮重试");
            }

            try
            {
                if (!await timer.WaitForNextTickAsync(stoppingToken)) break;
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task EvaluateAndNotifyAsync(INotificationTransport transport, TimeSpan repeat, CancellationToken ct)
    {
        var dbDown = !await ProbeDbAsync(ct);
        var verdict = _evaluator.Evaluate(dbDown);
        var now = DateTimeOffset.Now;

        var action = Decide(verdict, _wasUnhealthy, _lastFingerprint, _lastSentAt, now, repeat);
        if (action == SystemAlertAction.None)
        {
            // 状态没变也没到重提醒的点：只同步内存态，不发。
            _wasUnhealthy = !verdict.Healthy;
            _lastFingerprint = verdict.Fingerprint();
            return;
        }

        var (title, lines) = BuildMessage(action, verdict, ResolveSiteName(_options), now);
        var channel = BuildChannel(_options);

        try
        {
            await transport.SendAsync(channel, title, lines, ct);
            _lastSentAt = now;
            _wasUnhealthy = !verdict.Healthy;
            _lastFingerprint = verdict.Fingerprint();
            _logger.LogInformation("系统告警已外发：{Title}", title);
        }
        catch (Exception ex)
        {
            // 不更新 _lastSentAt 与状态记忆：下一轮会重新判定并重试。
            _logger.LogError(ex, "系统告警外发失败（{Title}），下一轮重试", title);
        }
    }

    private async Task<bool> ProbeDbAsync(CancellationToken ct)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            await using var cmd = _dataSource.CreateCommand("SELECT 1");
            await cmd.ExecuteScalarAsync(timeout.Token);
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // 是进程在停，不是库挂了。吞掉这个会让关机时判成 db_down，
            // 然后试着发一条根本不存在的故障，日志里留一条误导下一个人的报错。
            throw;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>站点名，留空取机器名。多站点不填的话，收到告警不知道是哪台机器。</summary>
    public static string ResolveSiteName(SystemAlertOptions options)
        => string.IsNullOrWhiteSpace(options.SiteName) ? Environment.MachineName : options.SiteName;

    /// <summary>
    /// 拼出发送用的渠道信息。刻意复用业务通知那套 <see cref="NotificationChannelInfo"/> 与 config 形状，
    /// 好让同一个适配器两边都能用；差别只在这份配置来自本地文件而不是 <c>notification_channels</c> 表。
    /// </summary>
    public static NotificationChannelInfo BuildChannel(SystemAlertOptions options)
        => new(
            Id: 0,
            Name: "系统告警",
            Kind: options.Kind,
            // 直接拼 JSON：字段固定三个，且都来自本地配置文件而非用户输入。
            // 仍走序列化器转义，防现场把带引号的 secret 粘进配置把报文搞坏。
            ConfigJson: System.Text.Json.JsonSerializer.Serialize(new
            {
                url = options.WebhookUrl,
                format = options.Format,
                secret = options.Secret,
            }));

    /// <summary>拼通知标题与正文。收到的人多半在手机上看，先说是哪台机器、什么时候，再说出了什么事。</summary>
    public static (string Title, IReadOnlyList<string> Lines) BuildMessage(
        SystemAlertAction action, SystemHealthVerdict verdict, string site, DateTimeOffset now)
    {
        var lines = new List<string> { $"站点：{site}", $"时间：{now:yyyy-MM-dd HH:mm:ss}" };

        if (action == SystemAlertAction.Recovered)
        {
            lines.Add("全部系统级故障已消除，监测恢复正常。");
            if (verdict.FreeDiskGb is not null)
                lines.Add($"磁盘可用：{verdict.FreeDiskGb} GB");
            return ($"BoltPreload 系统恢复：{site}", lines);
        }

        foreach (var reason in verdict.DegradeReasons)
            lines.Add(DescribeReason(reason, verdict));
        if (verdict.DeadServices.Count > 0)
            lines.Add($"后台服务失联：{string.Join("、", verdict.DeadServices)}（这些服务停了，对应的监测能力已经没有了）");

        return ($"BoltPreload 系统故障：{site}", lines);
    }

    /// <summary>降级理由的中文说明。与前端 AppShell / SystemHealth 的文案保持同一口径。</summary>
    private static string DescribeReason(string reason, SystemHealthVerdict verdict) => reason switch
    {
        "db_down" => "数据库不可达：测量未入库，此期间监测结论不可信",
        "db_write_failing" => "数据入库持续失败（降级）：此窗口内预警级告警不评估，仅严重档补报",
        "wal_saturated" => "断网缓存已满，最旧的测量正在被永久丢弃",
        // 带上盘符：现场多半把数据盘和系统盘分开，不说清是哪个盘等于让人去猜
        "low_disk" => $"磁盘余量不足：{verdict.TightestDiskRoot ?? "本机"} 仅剩 {verdict.FreeDiskGb} GB，"
                      + "缓存、波形与日志即将写不下",
        _ => reason,
    };

    /// <summary>
    /// 这一轮该不该发、发哪种。抽成纯函数便于回归：状态机错了的表现是"该报的不报"或"每分钟刷屏"，
    /// 两种都会让人把这个通知静音，等于没有。
    /// </summary>
    /// <param name="verdict">本轮判定结果。</param>
    /// <param name="wasUnhealthy">上一轮是否处于故障态。</param>
    /// <param name="lastFingerprint">上一轮的故障指纹。</param>
    /// <param name="lastSentAt">上一次成功发送的时刻（从未发送为 default）。</param>
    /// <param name="now">当前时刻。</param>
    /// <param name="repeat">持续故障的重提醒间隔。</param>
    public static SystemAlertAction Decide(
        SystemHealthVerdict verdict,
        bool wasUnhealthy,
        string lastFingerprint,
        DateTimeOffset lastSentAt,
        DateTimeOffset now,
        TimeSpan repeat)
    {
        if (verdict.Healthy)
            return wasUnhealthy ? SystemAlertAction.Recovered : SystemAlertAction.None;

        if (!wasUnhealthy)
            return SystemAlertAction.Fault;                    // 刚出问题

        if (!string.Equals(verdict.Fingerprint(), lastFingerprint, StringComparison.Ordinal))
            return SystemAlertAction.Fault;                    // 故障面变了（又坏一样，或好了一样）

        return now - lastSentAt >= repeat
            ? SystemAlertAction.Fault                          // 一直没恢复，按间隔提醒
            : SystemAlertAction.None;
    }
}

/// <summary>系统告警这一轮的动作。</summary>
public enum SystemAlertAction
{
    /// <summary>不发。</summary>
    None,

    /// <summary>发故障通知（新故障、故障面变化、或持续未恢复的重提醒）。</summary>
    Fault,

    /// <summary>发恢复通知。</summary>
    Recovered,
}

/// <summary>系统告警外发配置（配置节 <c>Notifications:SystemAlert</c>）。</summary>
public sealed class SystemAlertOptions
{
    /// <summary>配置节名。</summary>
    public const string SectionName = "Notifications:SystemAlert";

    /// <summary>是否启用。默认关闭；生产环境不开等于系统自己出事没人知道，启动日志会警告。</summary>
    public bool Enabled { get; set; }

    /// <summary>接收地址。企业微信/钉钉群机器人地址或自有接收端点。</summary>
    public string WebhookUrl { get; set; } = "";

    /// <summary>渠道类型，对应 <see cref="INotificationTransport.Kind"/>。当前只有 webhook。</summary>
    public string Kind { get; set; } = "webhook";

    /// <summary>报文形状：raw / wecom / dingtalk，与业务通知渠道同一套。</summary>
    public string Format { get; set; } = "raw";

    /// <summary>raw 走请求头令牌，dingtalk 走加签；wecom 不用。</summary>
    public string Secret { get; set; } = "";

    /// <summary>评估间隔（秒），下限 10。</summary>
    public int CheckIntervalSeconds { get; set; } = 60;

    /// <summary>故障持续未恢复时的重提醒间隔（分钟），下限 1。</summary>
    public int RepeatMinutes { get; set; } = 30;

    /// <summary>启动宽限（秒）：这段时间内不发，避开进程刚起来库还没就绪、后台服务还没打心跳的假故障。</summary>
    public int StartupGraceSeconds { get; set; } = 60;

    /// <summary>站点名，出现在通知标题里。留空取机器名。多站点时务必填，否则收到告警不知道是哪台。</summary>
    public string SiteName { get; set; } = "";
}
