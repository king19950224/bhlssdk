using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace BoltPreload.Tests.Integration;

/// <summary>
/// 仿真模式生产防线：Fx:Mode=Simulated 在 Production 环境必须拒绝启动——
/// 模拟器产出物理自洽、看似正常的假预紧力，误配到现场等于把仿真数据当真实监测，
/// 属重大安全隐患（商用底线）。防线是启动的第一道检查，先于连接串等一切校验，故本测试无需 Postgres。
/// </summary>
public sealed class SimulatedModeGuardTests
{
    [Fact]
    public void SimulatedMode_InProduction_RefusesToStart()
    {
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseEnvironment("Production");
            b.UseSetting("Fx:Mode", "Simulated");
        });

        var act = () => factory.CreateClient();
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*Simulated 禁止在 Production 环境*");
    }
}
