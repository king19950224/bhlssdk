using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace BoltPreload.Tests.Integration;

/// <summary>
/// 锁住仿真模式的生产防线：Production 环境配了 Fx:Mode=Simulated 就必须起不来。
/// 模拟器的假预紧力在界面上跟真的没区别，误配到现场就是拿仿真数据当监测结果看。
/// 这道检查在启动最前面，连接串都还没校验，所以这个测试不需要 Postgres。
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
            .WithMessage("*Production 环境不许跑 Fx:Mode=Simulated*");
    }
}
