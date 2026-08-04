using BoltPreload.Api.Auth;
using FluentAssertions;
using Xunit;

namespace BoltPreload.Tests.Api;

/// <summary>
/// 授权容量判定（<see cref="LicenseEnforcementState"/>）。
/// <para>
/// 这里每个比较符都直接对着钱和现场：判严一档，正好买满 40 通道的正版客户一上线就被锁死写操作，
/// 标定、下发、改阈值全做不了；判松一档，超装的部署照常配置，容量条款形同虚设。
/// 之前这个类零测试。
/// </para>
/// </summary>
public class LicenseEnforcementStateTests
{
    private static LicenseEnforcementState Evaluated(int devices, int channels, int? maxDevices, int? maxChannels)
    {
        var s = new LicenseEnforcementState();
        s.Set(devices, channels, maxDevices, maxChannels);
        return s;
    }

    [Fact]
    public void BeforeEvaluation_DefaultsToPermissive()
    {
        var s = new LicenseEnforcementState();

        // 启动早期（评估尚未跑到）不能误伤：中间件靠 Evaluated 决定要不要看这个结论
        s.Evaluated.Should().BeFalse();
        s.WithinLimits.Should().BeTrue();
        s.Violation.Should().BeNull();
    }

    [Theory]
    [InlineData(39, false, "未用满，放行")]
    [InlineData(40, false, "恰好用满授权额度，仍应放行")]
    [InlineData(41, true, "超出一路即判超限")]
    public void ChannelLimit_IsInclusiveAtExactlyTheCap(int channels, bool shouldViolate, string why)
    {
        var s = Evaluated(devices: 1, channels: channels, maxDevices: 2, maxChannels: 40);

        s.WithinLimits.Should().Be(!shouldViolate, why);
        s.Evaluated.Should().BeTrue();
    }

    [Theory]
    [InlineData(2, false, "恰好用满设备额度，仍应放行")]
    [InlineData(3, true, "多接一台即判超限")]
    public void DeviceLimit_IsInclusiveAtExactlyTheCap(int devices, bool shouldViolate, string why)
    {
        var s = Evaluated(devices: devices, channels: 1, maxDevices: 2, maxChannels: 40);

        s.WithinLimits.Should().Be(!shouldViolate, why);
    }

    [Fact]
    public void NullCaps_MeanUnlimited_NotZero()
    {
        // license 未声明容量时字段为 null。若把 null 当 0 处理，任何真实部署都会立刻超限。
        var s = Evaluated(devices: 999, channels: 9999, maxDevices: null, maxChannels: null);

        s.WithinLimits.Should().BeTrue();
        s.Violation.Should().BeNull();
    }

    [Fact]
    public void BothOverLimit_ReportsEachViolationWithNumbers()
    {
        var s = Evaluated(devices: 5, channels: 100, maxDevices: 2, maxChannels: 40);

        s.WithinLimits.Should().BeFalse();
        // 这段文案会原样进 403 响应和运维日志，现场据此判断该补几台的授权
        s.Violation.Should().Contain("设备数 5 超过授权上限 2")
            .And.Contain("通道数 100 超过授权上限 40");
    }

    [Fact]
    public void Reevaluation_ClearsPreviousViolation()
    {
        var s = new LicenseEnforcementState();
        s.Set(5, 100, 2, 40);
        s.WithinLimits.Should().BeFalse();

        // 补足授权后重新评估必须真的解锁，不能残留上一轮的 Violation 把写操作一直锁着
        s.Set(5, 100, 10, 200);
        s.WithinLimits.Should().BeTrue();
        s.Violation.Should().BeNull();
        s.MaxChannels.Should().Be(200);
    }
}
