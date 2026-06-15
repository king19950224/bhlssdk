namespace BoltPreload.Domain.Entities;

/// <summary>
/// 监测组。业务分组，如"3 号风机塔筒上法兰，含 12 颗螺栓"。
/// </summary>
public class MonitorGroup
{
    public long Id { get; set; }

    public long ProjectId { get; set; }

    public string Code { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    /// <summary>flange / tower / support_seat / ...</summary>
    public string? StructureType { get; set; }

    public short? ExpectedBoltCount { get; set; }

    /// <summary>组级目标预紧力（牛）</summary>
    public decimal? TargetPreloadN { get; set; }

    public bool Enabled { get; set; } = true;

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public DateTimeOffset? DeletedAt { get; set; }
}
