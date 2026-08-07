namespace BoltPreload.Domain.Entities;

/// <summary>
/// 物理终端设备，对应 SDK 的 sessionId。
/// 一台设备可以有 N 个通道。
/// </summary>
public class Device
{
    public long Id { get; set; }

    public long ProjectId { get; set; }

    public string Code { get; set; } = string.Empty;

    /// <summary>SDK model: 0 标准 / 1 专家 / 2 实时24通道 / 3 多通道监测</summary>
    public short Model { get; set; }

    /// <summary>0 串口 / 1 TCPClient / 2 TCPServer</summary>
    public short CommType { get; set; }

    /// <summary>"192.168.0.7:23" / "COM3:115200,0,8,1" / "192.168.0.7:23,6000"</summary>
    public string CommParams { get; set; } = string.Empty;

    public short Address { get; set; } = 1;

    /// <summary>0 LoraMesh / 1 直连</summary>
    public short TransferMode { get; set; } = 1;

    /// <summary>物理通道数。多通道监测设备现场默认 40。</summary>
    public short ChannelCount { get; set; } = 40;

    public bool Enabled { get; set; } = true;

    /// <summary>探头在位自动建档归属监测组（0026_device_default_group）。</summary>
    public long? DefaultGroupId { get; set; }

    // 计量溯源（§9.3）
    public string? CalibCertNo { get; set; }
    public DateOnly? CalibDate { get; set; }
    public DateOnly? NextCalibDate { get; set; }
    public string? CalibInstitution { get; set; }

    /// <summary>最近一次成功通信时间，运维监控用</summary>
    public DateTimeOffset? LastSeenAt { get; set; }

    /// <summary>online / offline / error / unknown</summary>
    public string State { get; set; } = "unknown";

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? DeletedAt { get; set; }
}
