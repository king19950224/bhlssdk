using BoltPreload.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace BoltPreload.Infrastructure.Persistence;

/// <summary>
/// 主数据库上下文。
/// <para>EF Core 用于业务表的 CRUD；时序表（measurements / measurement_anomalies）走原生 SQL 批量写，避免 EF 的 change tracking 开销。</para>
/// <para>schema 不通过 EF Migrations 管理（避免和手写 SQL 冲突），由 <c>deploy/sql/</c> 下的版本化脚本管理。</para>
/// </summary>
public sealed class BoltPreloadDbContext : DbContext
{
    public BoltPreloadDbContext(DbContextOptions<BoltPreloadDbContext> options) : base(options) { }

    public DbSet<Project> Projects => Set<Project>();
    public DbSet<MonitorGroup> MonitorGroups => Set<MonitorGroup>();
    public DbSet<Device> Devices => Set<Device>();
    public DbSet<Channel> Channels => Set<Channel>();
    public DbSet<Bolt> Bolts => Set<Bolt>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // 全表统一前缀（无）+ 表名小写复数
        // 对应 deploy/sql/0001_initial_schema.sql

        modelBuilder.Entity<Project>(e =>
        {
            e.ToTable("projects");
            e.HasKey(x => x.Id);
            MapTimestamps(e);
            e.Property(x => x.Code).HasColumnName("code").IsRequired().HasMaxLength(32);
            e.Property(x => x.Name).HasColumnName("name").IsRequired().HasMaxLength(128);
            e.Property(x => x.Industry).HasColumnName("industry").IsRequired().HasMaxLength(32);
            e.Property(x => x.Location).HasColumnName("location").HasMaxLength(256);
            e.Property(x => x.OwnerCompany).HasColumnName("owner_company").HasMaxLength(256);
            e.Property(x => x.ContractNo).HasColumnName("contract_no").HasMaxLength(64);
            e.Property(x => x.SlaLevel).HasColumnName("sla_level").IsRequired().HasMaxLength(16);
            e.Property(x => x.Enabled).HasColumnName("enabled");
            e.HasIndex(x => x.Code).IsUnique();
            e.HasQueryFilter(x => x.DeletedAt == null);
        });

        modelBuilder.Entity<MonitorGroup>(e =>
        {
            e.ToTable("monitor_groups");
            e.HasKey(x => x.Id);
            MapTimestamps(e);
            e.Property(x => x.ProjectId).HasColumnName("project_id");
            e.Property(x => x.TurbineId).HasColumnName("turbine_id");
            e.Property(x => x.Code).HasColumnName("code").IsRequired().HasMaxLength(64);
            e.Property(x => x.Name).HasColumnName("name").IsRequired().HasMaxLength(128);
            e.Property(x => x.StructureType).HasColumnName("structure_type").HasMaxLength(32);
            e.Property(x => x.ExpectedBoltCount).HasColumnName("expected_bolt_count");
            e.Property(x => x.TargetPreloadN).HasColumnName("target_preload_n").HasPrecision(12, 2);
            e.Property(x => x.Enabled).HasColumnName("enabled");
            e.HasIndex(x => new { x.ProjectId, x.Code }).IsUnique();
            e.HasQueryFilter(x => x.DeletedAt == null);
        });

        modelBuilder.Entity<Device>(e =>
        {
            e.ToTable("devices");
            e.HasKey(x => x.Id);
            MapTimestamps(e);
            e.Property(x => x.ProjectId).HasColumnName("project_id");
            e.Property(x => x.Code).HasColumnName("code").IsRequired().HasMaxLength(64);
            e.Property(x => x.Model).HasColumnName("model");
            e.Property(x => x.CommType).HasColumnName("comm_type");
            e.Property(x => x.CommParams).HasColumnName("comm_params").IsRequired().HasMaxLength(128);
            e.Property(x => x.Address).HasColumnName("address");
            e.Property(x => x.TransferMode).HasColumnName("transfer_mode");
            e.Property(x => x.ChannelCount).HasColumnName("channel_count");
            e.Property(x => x.Enabled).HasColumnName("enabled");
            e.Property(x => x.DefaultGroupId).HasColumnName("default_group_id");
            e.Property(x => x.CalibCertNo).HasColumnName("calib_cert_no").HasMaxLength(64);
            e.Property(x => x.CalibDate).HasColumnName("calib_date");
            e.Property(x => x.NextCalibDate).HasColumnName("next_calib_date");
            e.Property(x => x.CalibInstitution).HasColumnName("calib_institution").HasMaxLength(128);
            e.Property(x => x.LastSeenAt).HasColumnName("last_seen_at");
            e.Property(x => x.State).HasColumnName("state").IsRequired().HasMaxLength(16);
            e.HasIndex(x => new { x.ProjectId, x.Code }).IsUnique();
            e.HasQueryFilter(x => x.DeletedAt == null);
        });

        modelBuilder.Entity<Channel>(e =>
        {
            e.ToTable("channels");
            e.HasKey(x => x.Id);
            MapTimestamps(e);
            e.Property(x => x.DeviceId).HasColumnName("device_id");
            e.Property(x => x.ChannelNo).HasColumnName("channel_no");
            e.Property(x => x.ProbeFreqMhz).HasColumnName("probe_freq_mhz").HasPrecision(5, 2);
            e.Property(x => x.Gain).HasColumnName("gain");
            e.Property(x => x.Threshold).HasColumnName("threshold");
            e.Property(x => x.EchoWinNs).HasColumnName("echo_win_ns").HasPrecision(12, 2);
            e.Property(x => x.EchoWidthUs).HasColumnName("echo_width_us");
            e.Property(x => x.PulseVoltage).HasColumnName("pulse_voltage");
            e.Property(x => x.PulseForward).HasColumnName("pulse_forward");
            e.Property(x => x.PulseReverse).HasColumnName("pulse_reverse");
            e.Property(x => x.MeasMode).HasColumnName("meas_mode");
            e.Property(x => x.TcMode).HasColumnName("tc_mode");
            e.Property(x => x.TempCoefficient).HasColumnName("temp_coefficient").HasPrecision(12, 10);
            e.Property(x => x.RefTimeNs).HasColumnName("ref_time_ns").HasPrecision(12, 2);
            e.Property(x => x.RefTempC).HasColumnName("ref_temp_c").HasPrecision(6, 2);
            e.Property(x => x.KL).HasColumnName("k_l").HasPrecision(20, 15);
            e.Property(x => x.KLUncertainty).HasColumnName("k_l_uncertainty").HasPrecision(20, 15);
            e.Property(x => x.ActiveCalibrationId).HasColumnName("active_calibration_id");
            e.Property(x => x.TemplateWaveUrl).HasColumnName("template_wave_url").HasMaxLength(512);
            e.Property(x => x.TofAlgorithm).HasColumnName("tof_algorithm").IsRequired().HasMaxLength(32);
            e.Property(x => x.Enabled).HasColumnName("enabled");
            e.Property(x => x.SamplingEnabled).HasColumnName("sampling_enabled");
            e.Property(x => x.HardwareState).HasColumnName("hardware_state").IsRequired().HasMaxLength(32);
            e.Property(x => x.BusinessState).HasColumnName("business_state").IsRequired().HasMaxLength(32);
            e.Property(x => x.ConfigSource).HasColumnName("config_source").IsRequired().HasMaxLength(32);
            e.Property(x => x.ConfigSyncedAt).HasColumnName("config_synced_at");
            e.Property(x => x.State).HasColumnName("state").IsRequired().HasMaxLength(32);
            e.Property(x => x.FirstSeenAt).HasColumnName("first_seen_at");
            e.Property(x => x.LastSeenAt).HasColumnName("last_seen_at");
            e.Property(x => x.DiscoveredAt).HasColumnName("discovered_at");
            e.HasIndex(x => new { x.DeviceId, x.ChannelNo }).IsUnique();
            e.HasQueryFilter(x => x.DeletedAt == null);
        });

        modelBuilder.Entity<Bolt>(e =>
        {
            e.ToTable("bolts");
            e.HasKey(x => x.Id);
            MapTimestamps(e);
            e.Property(x => x.GroupId).HasColumnName("group_id");
            e.Property(x => x.ChannelId).HasColumnName("channel_id");
            e.Property(x => x.Code).HasColumnName("code").IsRequired().HasMaxLength(64);
            e.Property(x => x.NominalDiam).HasColumnName("nominal_diam").IsRequired().HasMaxLength(8);
            e.Property(x => x.Grade).HasColumnName("grade").IsRequired().HasMaxLength(8);
            e.Property(x => x.Rp02Mpa).HasColumnName("rp02_mpa").HasPrecision(8, 2);
            e.Property(x => x.Material).HasColumnName("material").IsRequired().HasMaxLength(32);
            e.Property(x => x.ElasticModulusGpa).HasColumnName("elastic_modulus_gpa").HasPrecision(6, 2);
            e.Property(x => x.EffectiveLengthMm).HasColumnName("effective_length_mm").HasPrecision(8, 2);
            e.Property(x => x.GripLengthMm).HasColumnName("grip_length_mm").HasPrecision(8, 2);
            e.Property(x => x.TotalLengthMm).HasColumnName("total_length_mm").HasPrecision(8, 2);
            e.Property(x => x.CrossSectionMm2).HasColumnName("cross_section_mm2").HasPrecision(8, 2);
            e.Property(x => x.SoundVelocityMs).HasColumnName("sound_velocity_ms").HasPrecision(8, 2);
            e.Property(x => x.TempCoefficientPerC).HasColumnName("temp_coefficient_per_c").HasPrecision(12, 10);
            e.Property(x => x.InstallTorqueNm).HasColumnName("install_torque_nm").HasPrecision(8, 2);
            e.Property(x => x.TargetPreloadN).HasColumnName("target_preload_n").HasPrecision(12, 2);
            e.Property(x => x.MinPreloadN).HasColumnName("min_preload_n").HasPrecision(12, 2);
            e.Property(x => x.PositionLabel).HasColumnName("position_label").HasMaxLength(64);
            e.Property(x => x.InstallDate).HasColumnName("install_date");
            e.Property(x => x.GbCompliant).HasColumnName("gb_compliant");
            e.Property(x => x.State).HasColumnName("state").IsRequired().HasMaxLength(16);
            e.Property(x => x.Enabled).HasColumnName("enabled");
            e.HasIndex(x => new { x.GroupId, x.Code }).IsUnique();
            e.HasQueryFilter(x => x.DeletedAt == null);
        });
    }

    private static void MapTimestamps<T>(Microsoft.EntityFrameworkCore.Metadata.Builders.EntityTypeBuilder<T> e) where T : class
    {
        e.Property("Id").HasColumnName("id");
        e.Property("CreatedAt").HasColumnName("created_at");
        e.Property("UpdatedAt").HasColumnName("updated_at");
        e.Property("DeletedAt").HasColumnName("deleted_at");
    }
}
