namespace BoltPreload.Domain.Entities;

/// <summary>
/// 项目（多租户边界）。如风电场、桥梁工程。
/// 对应数据库表 projects。
/// </summary>
public class Project
{
    public long Id { get; set; }

    public string Code { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    /// <summary>风电/桥梁/法兰/...</summary>
    public string Industry { get; set; } = string.Empty;

    public string? Location { get; set; }

    public string? OwnerCompany { get; set; }

    public string? ContractNo { get; set; }

    /// <summary>SLA 等级：standard / premium</summary>
    public string SlaLevel { get; set; } = "standard";

    public bool Enabled { get; set; } = true;

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public DateTimeOffset? DeletedAt { get; set; }
}
