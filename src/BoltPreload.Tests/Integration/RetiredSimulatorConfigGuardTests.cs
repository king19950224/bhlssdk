using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace BoltPreload.Tests.Integration;

/// <summary>
/// 锁住残留仿真配置的防线：设备模拟器已整体移除，任何还写着 Fx:Mode=Simulated 的
/// 配置文件、环境变量或部署脚本都必须让进程起不来。
/// <para>
/// 为什么不是"忽略它就好"：忽略等于让运维以为自己跑的是仿真、实际连的是真机，
/// 或反过来以为有仿真数据兜底。配置与实际跑的不是一回事，这种状态本身就是故障源。
/// 这道检查在启动最前面，连接串都还没校验，所以本测试不需要 Postgres。
/// </para>
/// </summary>
public sealed class RetiredSimulatorConfigGuardTests
{
    [Theory]
    [InlineData("Simulated")]
    [InlineData("simulated")]
    [InlineData("Sim")]
    public void ResidualSimulatedMode_RefusesToStart(string mode)
    {
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseEnvironment("Development");
            b.UseSetting("Fx:Mode", mode);
        });

        var act = () => factory.CreateClient();
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*设备模拟器已整体移除*");
    }

    /// <summary>
    /// 显式写 Fx:Mode=Real 的存量配置不该被这道检查连坐——现场大量部署脚本里写着它，
    /// 拒绝启动会把升级变成一次停机事故。它只是冗余，不是错误。
    /// </summary>
    [Fact]
    public void ExplicitRealMode_IsStillAccepted()
    {
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseEnvironment("Development");
            b.UseSetting("Fx:Mode", "Real");
            b.UseSetting("ConnectionStrings:Postgres", "Host=127.0.0.1;Port=1;Database=x;Username=x;Password=x");
        });

        // 不断言能起来（没有 Postgres 起不来），只断言不是被这道仿真检查拦下的
        var act = () => factory.CreateClient();
        act.Should().NotThrow<InvalidOperationException>(
            "Fx:Mode=Real 只是冗余配置，不该被残留仿真配置的检查拦住");
    }
}
