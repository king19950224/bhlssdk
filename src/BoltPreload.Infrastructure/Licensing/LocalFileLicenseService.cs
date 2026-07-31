using System.Net.NetworkInformation;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BoltPreload.Application.Licensing;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BoltPreload.Infrastructure.Licensing;

/// <summary>本地授权文件配置（appsettings 的 License 节）。</summary>
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

        var macs = CollectCandidateMacs();
        MachineCode = ComputeMachineCode(macs[0]);
        _acceptedMachineCodes = macs.Select(ComputeMachineCode).ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>对外公布、供签发方签发用的机器码（候选集里最小 MAC 那一个）。</summary>
    public string MachineCode { get; }

    /// <summary>
    /// 验证时【接受】的机器码全集：本机每块物理网卡各算一个。
    /// 只要 license 命中其中任意一个即视为同机，详见 <see cref="CollectCandidateMacs"/>。
    /// </summary>
    private readonly HashSet<string> _acceptedMachineCodes;

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

            var machineMismatch = !_acceptedMachineCodes.Contains(doc.MachineCode ?? "");
            var expired = DateOnly.FromDateTime(DateTime.UtcNow) > doc.ExpiresAt;
            var signatureValid = VerifySignature(doc, out var sigError);

            var valid = !machineMismatch && !expired && signatureValid;

            var warning = valid
                ? null
                : string.Join("; ", new[]
                {
                    machineMismatch
                        ? $"机器码不匹配：license={doc.MachineCode}，本机可接受={string.Join("/", _acceptedMachineCodes)}"
                        : null,
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
    /// 机器码 = SHA-256(MachineName | MAC | OS | CPU 核数) 截前 16 hex。
    /// 字段顺序与历史版本一致，变的只是"用哪块网卡的 MAC"（见 <see cref="CollectCandidateMacs"/>）。
    /// </summary>
    private static string ComputeMachineCode(string mac)
    {
        var input = string.Join("|", new[]
        {
            Environment.MachineName,
            mac,
            Environment.OSVersion.Platform.ToString(),
            Environment.ProcessorCount.ToString(System.Globalization.CultureInfo.InvariantCulture)
        });
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(hash)[..16];
    }

    /// <summary>
    /// 收集本机物理网卡 MAC，作为机器码的候选来源（升序，元素唯一，至少一项）。
    /// <para>
    /// <b>为什么不是"挑一块网卡"</b>：旧实现取“<c>OperationalStatus == Up</c> + 按网卡名排序的第一块”，
    /// 这让机器码会自己漂移，实测同一台机器 30 分钟内就变了一次：
    /// </para>
    /// <list type="bullet">
    /// <item>按<b>名字</b>排序会选中 <c>awdl0</c>（Apple 点对点临时接口）这类虚拟网卡，
    ///       它自己上下线，且 MAC 每次会话随机；Windows 上对应 Hyper-V/WSL/VPN 虚拟适配器。</item>
    /// <item>只认 <c>Up</c> 意味着<b>拔掉现场网线就换机器码</b> —— 授权随即失效，
    ///       LicenseGate 拦掉全部写操作，值班人员在网络最不稳的时候连告警都确认不了。
    ///       对安全监测系统，误锁远比放宽绑定危险。</item>
    /// </list>
    /// <para>
    /// 现在的口径：<b>不看链路状态</b>（通断不是身份）；只取物理类型网卡；优先只用全局唯一
    /// （universally administered）MAC —— 本地管理位为 1 的多是虚拟网卡与随机化地址，会话之间会变；
    /// 按 MAC 升序，与网卡命名和枚举顺序无关。验证时接受任一候选算出的码（见 <see cref="_acceptedMachineCodes"/>），
    /// 于是插拔网卡、虚拟适配器来去都不会让已签发的授权失效，换整机仍然匹配不上。
    /// </para>
    /// </summary>
    private static List<string> CollectCandidateMacs()
    {
        try
        {
            var all = NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.NetworkInterfaceType is NetworkInterfaceType.Ethernet
                                                  or NetworkInterfaceType.Wireless80211
                                                  or NetworkInterfaceType.GigabitEthernet)
                .Select(n => Convert.ToHexString(n.GetPhysicalAddress().GetAddressBytes()))
                .Where(mac => mac.Length == 12 && mac != "000000000000")
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            var universal = all.Where(IsUniversallyAdministered).ToList();
            var chosen = universal.Count > 0 ? universal : all;
            chosen.Sort(StringComparer.OrdinalIgnoreCase);

            // 没有任何可用网卡（极简镜像/网卡全禁用）：退化成固定占位，机器码仍由机器名等决定，
            // 保证行为确定 —— 绝不能返回空集让下标越界。
            return chosen.Count > 0 ? chosen : ["NO_MAC"];
        }
        catch
        {
            return ["NO_MAC"];
        }
    }

    /// <summary>
    /// MAC 首字节 bit1（0x02）为 0 表示全局唯一（厂商烧号）；为 1 是本地管理地址，
    /// 常见于虚拟网卡、VPN、以及 macOS/iOS 的随机化 Wi-Fi 地址，不适合作为机器身份。
    /// </summary>
    private static bool IsUniversallyAdministered(string mac)
        => (Convert.ToByte(mac[..2], 16) & 0x02) == 0;
}
