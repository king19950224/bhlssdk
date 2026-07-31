using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BoltPreload.Application.Licensing;
using BoltPreload.Infrastructure.Licensing;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace BoltPreload.Tests.Infrastructure;

/// <summary>
/// LocalFileLicenseService 授权有效性回归测试。
/// 重点：AllowMissingLicenseInDevelopment 只能在【真正的 Development 环境】生效，
/// 防止该开关被误配到生产导致无 license 也"有效"（商用安全底线）。
/// </summary>
public class LocalFileLicenseServiceTests
{
    private static readonly JsonSerializerOptions LicenseJson = new(JsonSerializerDefaults.Web);

    private static LocalFileLicenseService NewService(bool allowMissing, bool isDevelopment)
    {
        var opt = new LocalFileLicenseOptions
        {
            // 指向一个保证不存在的路径，命中"缺 license"分支
            LicensePath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"no_license_{System.Guid.NewGuid():N}.json"),
            AllowMissingLicenseInDevelopment = allowMissing,
            IsDevelopmentEnvironment = isDevelopment,
        };
        return new LocalFileLicenseService(Options.Create(opt), NullLogger<LocalFileLicenseService>.Instance);
    }

    [Fact]
    public void MissingLicense_Development_WithFlag_IsValid()
    {
        var status = NewService(allowMissing: true, isDevelopment: true).GetStatus();
        status.Found.Should().BeFalse();
        status.Valid.Should().BeTrue("开发环境 + 显式允许时，缺 license 可运行");
    }

    [Fact]
    public void MissingLicense_Production_WithFlag_IsInvalid()
    {
        // 关键安全用例：即使配置文件里 AllowMissingLicenseInDevelopment=true，
        // 非 Development 环境也必须无效，绝不能让生产裸奔。
        var status = NewService(allowMissing: true, isDevelopment: false).GetStatus();
        status.Valid.Should().BeFalse("非 Development 环境必须忽略该开关，按无效授权处理");
    }

    [Fact]
    public void MissingLicense_Development_NoFlag_IsInvalid()
    {
        var status = NewService(allowMissing: false, isDevelopment: true).GetStatus();
        status.Valid.Should().BeFalse();
    }

    [Fact]
    public void MissingLicense_Production_NoFlag_IsInvalid()
    {
        var status = NewService(allowMissing: false, isDevelopment: false).GetStatus();
        status.Valid.Should().BeFalse();
    }

    /// <summary>
    /// 机器码必须在同一台机器上稳定。旧实现取「OperationalStatus==Up + 按网卡名排序的第一块」，
    /// 会被 awdl0 / VPN / Hyper-V 这类虚拟网卡的上下线改掉，实测同机 30 分钟内变过一次 ——
    /// 后果是已签发的授权自己失效、现场写操作全被 403 拦掉。
    /// </summary>
    [Fact]
    public void MachineCode_IsStable_AcrossInstances()
    {
        var a = NewService(allowMissing: false, isDevelopment: false).MachineCode;
        var b = NewService(allowMissing: false, isDevelopment: false).MachineCode;

        a.Should().NotBeNullOrWhiteSpace();
        a.Should().HaveLength(16);
        b.Should().Be(a, "同一台机器每次算出的机器码必须一致，否则授权会自己失效");
    }

    /// <summary>
    /// 端到端：按 tools/LicenseSigner 的同一套规范化payload 签一份 license，服务必须判为有效。
    /// <para>
    /// 这条用例守的是一个曾经真实存在的交付缺口：仓库里只有验签端、没有任何能签出合法 license 的东西，
    /// 交付出去后 LicenseGate 会拦掉全部写操作 —— 建档、标定、告警确认统统 403，系统"能看不能用"。
    /// 只要签发与验签两端的拼接口径漂移，本用例即失败。
    /// </para>
    /// </summary>
    [Fact]
    public void SignedLicense_ForThisMachine_IsValid()
    {
        using var rsa = RSA.Create(2048);
        var probe = NewService(allowMissing: false, isDevelopment: false);

        var path = Path.Combine(Path.GetTempPath(), $"license_{Guid.NewGuid():N}.json");
        try
        {
            WriteSignedLicense(path, rsa, probe.MachineCode);
            var status = Service(path, rsa.ExportSubjectPublicKeyInfoPem()).GetStatus();

            status.Found.Should().BeTrue();
            status.Valid.Should().BeTrue($"签名与机器码都正确时必须有效；实际告警：{status.Warning}");
            status.Expired.Should().BeFalse();
            status.MaxChannels.Should().Be(40);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void SignedLicense_TamperedField_IsInvalid()
    {
        using var rsa = RSA.Create(2048);
        var probe = NewService(allowMissing: false, isDevelopment: false);

        var path = Path.Combine(Path.GetTempPath(), $"license_{Guid.NewGuid():N}.json");
        try
        {
            WriteSignedLicense(path, rsa, probe.MachineCode);
            // 签发后手工把通道容量从 40 抬到 400：签名覆盖该字段，必须验不过
            File.WriteAllText(path, File.ReadAllText(path).Replace("\"maxChannels\":40", "\"maxChannels\":400",
                StringComparison.Ordinal));

            var status = Service(path, rsa.ExportSubjectPublicKeyInfoPem()).GetStatus();
            status.Valid.Should().BeFalse("改过容量字段的 license 必须验签失败");
            status.Warning.Should().Contain("签名");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void SignedLicense_ForAnotherMachine_IsInvalid()
    {
        using var rsa = RSA.Create(2048);
        var path = Path.Combine(Path.GetTempPath(), $"license_{Guid.NewGuid():N}.json");
        try
        {
            WriteSignedLicense(path, rsa, machineCode: "0000000000000000");
            var status = Service(path, rsa.ExportSubjectPublicKeyInfoPem()).GetStatus();

            status.Valid.Should().BeFalse("签给别的机器码不能在本机生效");
            status.Warning.Should().Contain("机器码不匹配");
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static LocalFileLicenseService Service(string licensePath, string publicKeyPem) =>
        new(Options.Create(new LocalFileLicenseOptions
        {
            LicensePath = licensePath,
            PublicKeyPem = publicKeyPem,
            AllowMissingLicenseInDevelopment = false,
            IsDevelopmentEnvironment = false,
        }), NullLogger<LocalFileLicenseService>.Instance);

    private static void WriteSignedLicense(string path, RSA rsa, string machineCode)
    {
        var doc = new LicenseDocument(
            LicenseId: "TEST-0001",
            LicensedTo: "回归测试客户",
            MachineCode: machineCode,
            ExpiresAt: DateOnly.FromDateTime(DateTime.UtcNow.AddYears(1)),
            MaxDevices: 2,
            MaxChannels: 40,
            Modules: ["realtime-monitoring", "reports"],
            Signature: "");

        var signature = Convert.ToBase64String(rsa.SignData(
            Encoding.UTF8.GetBytes(LocalFileLicenseService.BuildSignaturePayload(doc)),
            HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));

        File.WriteAllText(path, JsonSerializer.Serialize(doc with { Signature = signature }, LicenseJson));
    }
}
