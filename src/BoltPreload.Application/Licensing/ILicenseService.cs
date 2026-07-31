namespace BoltPreload.Application.Licensing;

/// <summary>
/// 商业授权服务。
/// <para>
/// 离线 license.json：机器码 + 通道数 + 设备数 + 到期时间 + RSA 签名。
/// 验签实现见 <c>LocalFileLicenseService</c>；签发/密钥生成用 <c>tools/LicenseSigner</c>
/// （流程见 docs/deployment-guide.md）。
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
