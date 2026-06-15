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
}
