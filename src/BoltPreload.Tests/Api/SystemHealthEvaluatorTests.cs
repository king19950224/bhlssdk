using BoltPreload.Api.Runtime;
using BoltPreload.Application.Diagnostics;
using BoltPreload.Application.Ingestion;
using BoltPreload.Infrastructure.Ingestion;
using FluentAssertions;
using Microsoft.Extensions.Options;
using Xunit;

namespace BoltPreload.Tests.Api;

/// <summary>
/// 系统级健康判定（<see cref="SystemHealthEvaluator"/>）。
/// <para>
/// 这一份口径同时喂给 <c>/api/health/ready</c> 与系统告警外发。两边此前各写各的，
/// 加磁盘判定时很容易只改一边，变成界面横幅报降级、通知一声不吭。所以判定收在这里，测试也钉在这里。
/// </para>
/// </summary>
public class SystemHealthEvaluatorTests
{
    private static SystemHealthEvaluator Build(
        IngestDegradeState? degrade = null,
        BackgroundServiceHealth? svc = null,
        double minFreeDiskGb = 5.0,
        string walRoot = "data/wal",
        string waveRoot = "data/waves")
        => new(
            degrade ?? new IngestDegradeState(),
            svc ?? new BackgroundServiceHealth(),
            Options.Create(new HealthThresholdOptions { MinFreeDiskGb = minFreeDiskGb }),
            Options.Create(new MeasurementWalOptions { RootPath = walRoot }),
            Options.Create(new LocalFileWaveStoreOptions { RootPath = waveRoot }));

    /// <summary>连续三次入库失败才进降级态（单条坏数据不该拉响全站）。</summary>
    private static IngestDegradeState Degraded()
    {
        var s = new IngestDegradeState();
        for (var i = 0; i < 3; i++) s.ReportDbWriteFailure();
        return s;
    }

    [Fact]
    public void AllGood_IsHealthy()
    {
        // 关掉磁盘判定，避免开发机盘紧的时候这条测试莫名其妙红
        var v = Build(minFreeDiskGb: 0).Evaluate();

        v.Healthy.Should().BeTrue();
        v.DegradeReasons.Should().BeEmpty();
        v.Fingerprint().Should().BeEmpty();
    }

    [Fact]
    public void DbDown_IsPassedThroughFromCaller()
    {
        // 判定本身不查库：库挂的时候这里还得出得了结论，再去查库就把自己也搭进去了
        var v = Build(minFreeDiskGb: 0).Evaluate(dbDown: true);

        v.DegradeReasons.Should().Contain("db_down");
        v.Healthy.Should().BeFalse();
    }

    [Fact]
    public void DbWriteDegraded_ShowsUp()
    {
        var v = Build(Degraded(), minFreeDiskGb: 0).Evaluate();

        v.DegradeReasons.Should().Contain("db_write_failing");
    }

    [Fact]
    public void WalDropped_ShowsUpAndSticks()
    {
        var degrade = new IngestDegradeState();
        degrade.ReportWalDropped(120);

        var v = Build(degrade, minFreeDiskGb: 0).Evaluate();

        // 丢弃是瞬时事件，但那段数据已经不完整了，不能丢完一条日志就当没发生
        v.DegradeReasons.Should().Contain("wal_saturated");
    }

    [Fact]
    public void LowDisk_EntersDegradeReasons()
    {
        // 阈值设成不可能满足的值，等价于"盘已经满了"。
        // 这一条是本次补的：此前磁盘余量只是 readiness 里的展示字段，盘写满之前探针一路绿灯。
        var v = Build(minFreeDiskGb: double.MaxValue).Evaluate();

        v.DegradeReasons.Should().Contain("low_disk");
        v.Healthy.Should().BeFalse();
    }

    [Fact]
    public void DiskCheckCanBeDisabled()
    {
        var v = Build(minFreeDiskGb: 0).Evaluate();

        v.DegradeReasons.Should().NotContain("low_disk", "阈值 <=0 表示关闭这项判定");
    }

    [Fact]
    public async Task StaleBackgroundService_IsReportedDead()
    {
        var svc = new BackgroundServiceHealth();
        svc.Beat("signal-health", TimeSpan.FromMilliseconds(1));   // 超过 3 倍间隔即判失联
        await Task.Delay(50);

        var v = Build(svc: svc, minFreeDiskGb: 0).Evaluate();

        // 进程活着但监测已停，是最难靠肉眼发现的一种故障
        v.DeadServices.Should().Contain("signal-health");
        v.Healthy.Should().BeFalse();
    }

    [Fact]
    public void FreshBackgroundService_IsNotDead()
    {
        var svc = new BackgroundServiceHealth();
        svc.Beat("ingestion", TimeSpan.FromMinutes(1));

        Build(svc: svc, minFreeDiskGb: 0).Evaluate().DeadServices.Should().BeEmpty();
    }

    [Fact]
    public void MultipleFaults_AreAllListed()
    {
        var degrade = Degraded();
        degrade.ReportWalDropped(5);

        var v = Build(degrade, minFreeDiskGb: double.MaxValue).Evaluate(dbDown: true);

        // 只报第一个的话，现场修完一个以为好了，实际还有两个在流血
        v.DegradeReasons.Should().Contain(["db_down", "db_write_failing", "wal_saturated", "low_disk"]);
    }

    [Fact]
    public void MinFreeDiskGb_IsExposedForTheProbePayload()
        => Build(minFreeDiskGb: 7.5).MinFreeDiskGb.Should().Be(7.5);

    [Fact]
    public void DiskProbe_ReportsWhichDriveIsTightest()
    {
        // 手册建议把 WAL 与波形挪到独立数据分区。只盯程序盘的话，真正会先写满的那块反而看不见。
        var v = Build(minFreeDiskGb: 0).Evaluate();

        v.FreeDiskGb.Should().NotBeNull();
        v.TightestDiskRoot.Should().NotBeNullOrWhiteSpace("告警文案要说清是哪个盘紧张，不能让人去猜");
    }

    [Fact]
    public void DiskProbe_TakesTheSmallestAcrossWatchedPaths()
    {
        var roots = DriveInfo.GetDrives()
            .Where(d => d.IsReady && d.DriveType == DriveType.Fixed)
            .ToArray();
        if (roots.Length < 2) return;   // 单盘开发机上这条没有可比性

        var (all, _) = SystemHealthEvaluator.ProbeFreeDiskGb(roots.Select(d => d.RootDirectory.FullName));
        var expected = roots.Min(d => Math.Round(d.AvailableFreeSpace / 1024.0 / 1024 / 1024, 1));

        all.Should().Be(expected, "多个盘时要取最紧张的那个，不然余量充足的盘会把快满的盘盖过去");
    }

    [Fact]
    public void DiskProbe_SurvivesBogusPaths()
    {
        // 盘符不存在、目录还没建（首次启动 data 目录尚未创建）都不该让整个判定失效
        var (free, root) = SystemHealthEvaluator.ProbeFreeDiskGb(
            ["Q:\\nope\\nowhere", "", AppContext.BaseDirectory]);

        free.Should().NotBeNull();
        root.Should().NotBeNullOrWhiteSpace();
    }
}
