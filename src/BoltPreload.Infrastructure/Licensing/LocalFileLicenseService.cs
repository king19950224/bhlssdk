using System.Net.NetworkInformation;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BoltPreload.Application.Licensing;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BoltPreload.Infrastructure.Licensing;

public sealed class LocalFileLicenseOptions
{
    public string LicensePath { get; set; } = "license.json";

    /// <summary>
    /// 缺 license.json 时是否允许进程以"有效授权"运行（仅供开发用）。
    /// 仅在 <see cref="IsDevelopmentEnvironment"/> 为 true 时才生效——即使配置文件里被误设为 true，
    /// 非 Development 环境也一律视为无效授权，防止生产意外裸奔。
    /// </summary>
    public bool AllowMissingLicenseInDevelopment { get; set; }

    /// <summary>
    /// 当前是否 Development 环境。由宿主在启动时按 IHostEnvironment 写入（PostConfigure），
    /// 默认 false（fail-safe：未显式标注 dev 时，缺 license 即无效）。
    /// </summary>
    public bool IsDevelopmentEnvironment { get; set; }

    /// <summary>
    /// RSA 公钥（SubjectPublicKeyInfo PEM 格式）。
    /// 生产环境必须提供；为空时所有非 dev 启动一律视为无效授权。
    /// </summary>
    public string PublicKeyPem { get; set; } = "";
}

/// <summary>
/// 文件式 License 服务（离线授权）。
/// <para>校验链：1) 文件存在；2) JSON 反序列化成功；3) 机器码匹配；4) 未过期；5) RSA 签名验证通过。</para>
/// <para>机器码：MachineName + 第一块物理网卡 MAC + OS 平台 + 处理器数 → SHA-256 截前 16 字符。
/// 比仅 MachineName/UserName 强很多（不能因用户改名而变），但仍能在硬件更换时由供应商重新签发。</para>
/// </summary>
public sealed class LocalFileLicenseService : ILicenseService
{
    private readonly LocalFileLicenseOptions _options;
    private readonly ILogger<LocalFileLicenseService> _logger;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public LocalFileLicenseService(IOptions<LocalFileLicenseOptions> options, ILogger<LocalFileLicenseService> logger)
    {
        _options = options.Value;
        _logger = logger;
        MachineCode = ComputeMachineCode();
    }

    public string MachineCode { get; }

    public LicenseStatus GetStatus()
    {
        if (!File.Exists(_options.LicensePath))
        {
            // 缺 license 只有在【真正的 Development 环境】且显式允许时才视为有效；生产一律无效。
            var allowMissing = _options.AllowMissingLicenseInDevelopment && _options.IsDevelopmentEnvironment;
            if (_options.AllowMissingLicenseInDevelopment && !_options.IsDevelopmentEnvironment)
                _logger.LogWarning("配置了 AllowMissingLicenseInDevelopment=true 但当前非 Development 环境，已忽略该开关，按无效授权处理。");

            return new LicenseStatus(
                Found: false,
                Valid: allowMissing,
                Expired: false,
                MachineCode,
                LicensedTo: null,
                LicenseId: null,
                ExpiresAt: null,
                MaxDevices: null,
                MaxChannels: null,
                Modules: Array.Empty<string>(),
                Warning: allowMissing
                    ? "未找到 license.json；开发模式允许运行，生产环境必须安装授权文件。"
                    : "未找到 license.json — 服务以无效授权状态运行。",
                Document: null);
        }

        try
        {
            var json = File.ReadAllText(_options.LicensePath);
            var doc = JsonSerializer.Deserialize<LicenseDocument>(json, JsonOptions);
            if (doc is null)
                return Invalid("license.json 格式错误", null);

            var machineMismatch = !string.Equals(doc.MachineCode, MachineCode, StringComparison.OrdinalIgnoreCase);
            var expired = DateOnly.FromDateTime(DateTime.UtcNow) > doc.ExpiresAt;
            var signatureValid = VerifySignature(doc, out var sigError);

            var valid = !machineMismatch && !expired && signatureValid;

            var warning = valid
                ? null
                : string.Join("; ", new[]
                {
                    machineMismatch ? $"机器码不匹配：license={doc.MachineCode}, local={MachineCode}" : null,
                    expired ? $"授权已过期：{doc.ExpiresAt}" : null,
                    !signatureValid ? $"签名校验失败：{sigError}" : null
                }.Where(x => x is not null));

            return new LicenseStatus(
                Found: true,
                Valid: valid,
                Expired: expired,
                MachineCode,
                LicensedTo: doc.LicensedTo,
                LicenseId: doc.LicenseId,
                ExpiresAt: doc.ExpiresAt,
                MaxDevices: doc.MaxDevices,
                MaxChannels: doc.MaxChannels,
                Modules: doc.Modules,
                Warning: warning,
                Document: doc);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "读取授权文件失败 {Path}", _options.LicensePath);
            return Invalid(ex.Message, null);
        }
    }

    /// <summary>
    /// RSA 签名验证（SHA-256 with PKCS1）。签名内容：
    ///   "{LicenseId}|{LicensedTo}|{MachineCode}|{ExpiresAt:yyyy-MM-dd}|{MaxDevices}|{MaxChannels}|{Modules}"
    /// 修改任意一个字段都会让签名失效。
    /// </summary>
    private bool VerifySignature(LicenseDocument doc, out string error)
    {
        error = "";
        if (string.IsNullOrWhiteSpace(doc.Signature))
        {
            error = "缺 signature 字段";
            return false;
        }
        if (string.IsNullOrWhiteSpace(_options.PublicKeyPem))
        {
            error = "未配置 License:PublicKeyPem，无法验签（生产必须提供）";
            return false;
        }

        try
        {
            using var rsa = RSA.Create();
            rsa.ImportFromPem(_options.PublicKeyPem);

            var canonical = BuildSignaturePayload(doc);
            var data = Encoding.UTF8.GetBytes(canonical);
            byte[] sig;
            try { sig = Convert.FromBase64String(doc.Signature); }
            catch (FormatException) { error = "signature 不是合法 Base64"; return false; }

            var ok = rsa.VerifyData(data, sig, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            if (!ok) error = "RSA 验签失败";
            return ok;
        }
        catch (Exception ex)
        {
            error = $"RSA 校验异常：{ex.Message}";
            return false;
        }
    }

    /// <summary>
    /// 生成签名时的规范化字符串。签发工具必须用一模一样的拼法。
    /// </summary>
    public static string BuildSignaturePayload(LicenseDocument doc) =>
        string.Join("|",
            doc.LicenseId,
            doc.LicensedTo,
            doc.MachineCode,
            doc.ExpiresAt.ToString("yyyy-MM-dd"),
            doc.MaxDevices.ToString(System.Globalization.CultureInfo.InvariantCulture),
            doc.MaxChannels.ToString(System.Globalization.CultureInfo.InvariantCulture),
            string.Join(",", doc.Modules));

    private LicenseStatus Invalid(string warning, LicenseDocument? doc)
        => new(
            Found: doc is not null,
            Valid: false,
            Expired: false,
            MachineCode,
            LicensedTo: doc?.LicensedTo,
            LicenseId: doc?.LicenseId,
            ExpiresAt: doc?.ExpiresAt,
            MaxDevices: doc?.MaxDevices,
            MaxChannels: doc?.MaxChannels,
            Modules: doc?.Modules ?? Array.Empty<string>(),
            Warning: warning,
            Document: doc);

    /// <summary>
    /// 机器码 = SHA-256(MachineName | 第一块物理网卡 MAC | OS | CPU 核数) 截前 16 hex。
    /// 历史版本只用 MachineName/UserName/OSVersion.Platform/ProcessorCount — 改个用户名就变，
    /// 克隆 VM 就完全一样。加入 MAC 之后强多了，但仍是供应商离线签发可控的范围。
    /// </summary>
    private static string ComputeMachineCode()
    {
        var input = string.Join("|", new[]
        {
            Environment.MachineName,
            GetPrimaryMacAddress(),
            Environment.OSVersion.Platform.ToString(),
            Environment.ProcessorCount.ToString(System.Globalization.CultureInfo.InvariantCulture)
        });
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(hash)[..16];
    }

    private static string GetPrimaryMacAddress()
    {
        try
        {
            // 取第一块 Up + 非环回 + 物理（Ethernet / WiFi）网卡 MAC
            var nic = NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up)
                .Where(n => n.NetworkInterfaceType is NetworkInterfaceType.Ethernet
                                                  or NetworkInterfaceType.Wireless80211
                                                  or NetworkInterfaceType.GigabitEthernet)
                .OrderBy(n => n.Name, StringComparer.Ordinal)
                .FirstOrDefault();
            if (nic is null) return "NO_MAC";
            var bytes = nic.GetPhysicalAddress().GetAddressBytes();
            return bytes.Length == 0 ? "NO_MAC" : Convert.ToHexString(bytes);
        }
        catch
        {
            return "NO_MAC";
        }
    }
}
