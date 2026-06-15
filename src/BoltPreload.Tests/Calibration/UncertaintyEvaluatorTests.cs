using BoltPreload.Calibration;
using FluentAssertions;
using Xunit;

namespace BoltPreload.Tests.Calibration;

/// <summary>
/// JJF 1059.1 不确定度评定引擎回归测试。
/// plan §8.5.2 明确：不确定度算错则整个标定证书作废 —— 故每步都单测覆盖。
/// </summary>
public class UncertaintyEvaluatorTests
{
    [Fact]
    public void TypeA_ComputesStandardUncertaintyOfMean()
    {
        // {1..10}: mean=5.5, s=√(82.5/9)=3.02765, u_A=s/√10=0.95743, ν=9
        var c = UncertaintyComponent.TypeA("u(Δt)", new double[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10 });

        c.Type.Should().Be(UncertaintyType.A);
        c.StandardUncertainty.Should().BeApproximately(0.957427, 1e-5);
        c.DegreesOfFreedom.Should().Be(9);
    }

    [Fact]
    public void TypeBUniform_DividesHalfWidthBySqrt3()
    {
        // 加载装置误差限 ±5（kN）→ u_B = 5/√3 ≈ 2.8868（plan §8.5.2 第4步算例）
        var c = UncertaintyComponent.TypeBUniform("u(F)", 5.0);
        c.StandardUncertainty.Should().BeApproximately(2.886751, 1e-5);

        // 半分辨率（分辨率 1ns 的一半）均匀分布 → 0.5/√3 ≈ 0.2887（plan u_B(div)）
        var div = UncertaintyComponent.TypeBUniform("u(div)", 0.5);
        div.StandardUncertainty.Should().BeApproximately(0.288675, 1e-5);
        div.DegreesOfFreedom.Should().Be(double.PositiveInfinity);
    }

    [Fact]
    public void Evaluate_CombinesInQuadrature_AndUsesK2ForLargeDof()
    {
        // 两个 B 类分量贡献 3 与 4（无关）→ u_c=5；均为 ∞ 自由度 → k=2 → U=10
        var components = new[]
        {
            UncertaintyComponent.TypeB("a", 3.0),
            UncertaintyComponent.TypeB("b", 4.0),
        };

        var budget = UncertaintyEvaluator.Evaluate(components);

        budget.CombinedStandardUncertainty.Should().BeApproximately(5.0, 1e-9);
        budget.EffectiveDegreesOfFreedom.Should().Be(double.PositiveInfinity);
        budget.CoverageFactor.Should().Be(2.0);
        budget.ExpandedUncertainty.Should().BeApproximately(10.0, 1e-9);
    }

    [Fact]
    public void Evaluate_AppliesSensitivityCoefficients()
    {
        // c·u：c=2,u=3 → 6；c=1,u=8 → 8；u_c=√(36+64)=10
        var components = new[]
        {
            UncertaintyComponent.TypeB("x1", 3.0, sensitivity: 2.0),
            UncertaintyComponent.TypeB("x2", 8.0, sensitivity: 1.0),
        };

        var budget = UncertaintyEvaluator.Evaluate(components);

        budget.CombinedStandardUncertainty.Should().BeApproximately(10.0, 1e-9);
    }

    [Fact]
    public void Evaluate_SmallEffectiveDof_UsesTDistributionK()
    {
        // 单个 A 类分量 ν=4（5 次重复）→ ν_eff=4 → k=t(0.975,4)=2.7764（而非 2）
        var c = UncertaintyComponent.TypeA("u", new double[] { 1, 2, 3, 4, 5 }); // ν=4, u_A=0.70711
        var budget = UncertaintyEvaluator.Evaluate(new[] { c });

        budget.EffectiveDegreesOfFreedom.Should().BeApproximately(4.0, 1e-9);
        budget.CoverageFactor.Should().BeApproximately(2.776445, 1e-4, "t(0.975, ν=4)=2.7764，小自由度不得用 k=2");
        budget.CombinedStandardUncertainty.Should().BeApproximately(0.707107, 1e-5);
        budget.ExpandedUncertainty.Should().BeApproximately(2.776445 * 0.707107, 1e-4);
    }

    [Fact]
    public void Evaluate_EmptyComponents_Throws()
    {
        var act = () => UncertaintyEvaluator.Evaluate(System.Array.Empty<UncertaintyComponent>());
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void TypeA_TooFewSamples_Throws()
    {
        var act = () => UncertaintyComponent.TypeA("u", new double[] { 1.0 });
        act.Should().Throw<ArgumentException>();
    }
}
