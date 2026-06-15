namespace BoltPreload.Application.Licensing;

/// <summary>
/// 商业授权服务。
/// <para>
/// Phase 1 先做离线 license.json：机器码 + 通道数 + 设备数 + 到期时间 + 签名字段。
/// Phase 2 再接正式签名校验（RSA/ECDSA）和授权生成工具。
/// </para>
/// </summary>
public interface ILicenseService
{
    string MachineCode { get; }

    LicenseStatus GetStatus();
}

public sealed record LicenseStatus(
    bool Found,
    bool Valid,
    bool Expired,
    string MachineCode,
    string? LicensedTo,
    string? LicenseId,
    DateOnly? ExpiresAt,
    int? MaxDevices,
    int? MaxChannels,
    IReadOnlyList<string> Modules,
    string? Warning,
    LicenseDocument? Document);

public sealed record LicenseDocument(
    string LicenseId,
    string LicensedTo,
    string MachineCode,
    DateOnly ExpiresAt,
    int MaxDevices,
    int MaxChannels,
    IReadOnlyList<string> Modules,
    string Signature);
