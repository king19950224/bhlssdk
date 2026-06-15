using BoltPreload.Algorithms.Preload;
using FluentAssertions;
using Xunit;

namespace BoltPreload.Tests.Algorithms;

/// <summary>
/// SimplifiedCalculator 单元测试。验证方案 §8.3 模型 A 公式 F = E·S·v·Δt / (2·L₀) 的实现。
/// </summary>
public class SimplifiedCalculatorTests
{
    /// <summary>
    /// 典型 M16 × 80mm 碳钢螺栓 8.8 级。
    /// </summary>
    private static BoltSpec M16_Carbon_8_8 => new(
        Grade: "8.8",
        Rp02Mpa: 640m,
        ElasticModulusGpa: 210m,
        NominalDiamMm: 16m,
        EffectiveLengthMm: 80m,
        CrossSectionMm2: 201m,            // π × (16/2)² ≈ 201
        SoundVelocityMs: 5890m);

    [Fact]
    public void Calculate_ZeroDeltaTof_GivesZeroPreload()
    {
        // 拧紧前后 TOF 一样 → 无应力变化 → F = 0
        var sut = new SimplifiedCalculator();
        var input = new PreloadInput(
            EchoTimeNs: 27200m,
            RefTimeNs: 27200m,
            TempC: 22m,
            RefTempC: 22m,
            TempCoefficient: 0.0001027m,
            Bolt: M16_Carbon_8_8,
            Calibration: null);

        var result = sut.Calculate(input);

        result.PreloadN.Should().NotBeNull();
        result.PreloadN!.Value.Should().BeApproximately(0m, 0.001m);
        result.DeltaTofNs.Should().BeApproximately(0m, 0.001m);
    }

    [Fact]
    public void Calculate_TypicalDeltaTof_GivesReasonablePreload()
    {
        // 用反推 Δt 让模型 A 在用标称 v=5890 下算出约 80 kN（M16 典型工作预紧力）：
        //   F_target = 80 kN
        //   Δt = F × 2 × L₀ / (E × S × v)
        //      = 80_000 × 2 × 0.080 / (210e9 × 201e-6 × 5890)
        //      = 12,800 / 2.486e11
        //      = 5.149e-8 s ≈ 51.5 ns
        // **注意**：这个 Δt = 51.5 ns 在标称 v 下对应 80 kN，但真机用 K_L 标定后可能是
        // Δt ≈ 200 ns 对应 80 kN（两者差几倍是模型 A 的本质局限，详见 SimplifiedCalculator 类 doc）
        var sut = new SimplifiedCalculator();
        var input = new PreloadInput(
            EchoTimeNs: 27200m + 51.5m,
            RefTimeNs: 27200m,
            TempC: 22m,
            RefTempC: 22m,
            TempCoefficient: 0.0001027m,
            Bolt: M16_Carbon_8_8,
            Calibration: null);

        var result = sut.Calculate(input);

        result.PreloadN.Should().NotBeNull();
        result.PreloadN!.Value.Should().BeInRange(70_000m, 90_000m,
            "Δt=51.5 ns 用模型 A 标称 v 应该给约 80 kN");

        // 不确定度按 ±20% 给
        result.UncertaintyN.Should().NotBeNull();
        var ratio = result.UncertaintyN!.Value / result.PreloadN.Value;
        ratio.Should().BeApproximately(0.20m, 0.001m);

        result.ModelUsed.Should().Be("Simplified");
        result.CalibrationId.Should().BeNull();
    }

    [Fact]
    public void Calculate_AcousticElasticEffectOverestimate_IsKnownLimitation()
    {
        // 文档化模型 A 的本质局限：直接拿真机标定下的 Δt（如 195 ns）套到模型 A 会高估几倍
        // 这是物理事实，不是 bug
        var sut = new SimplifiedCalculator();
        var input = new PreloadInput(
            EchoTimeNs: 27200m + 195m,       // 真机标定档案里 ~90 kN 对应的 Δt
            RefTimeNs: 27200m,
            TempC: 22m,
            RefTempC: 22m,
            TempCoefficient: 0.0001027m,
            Bolt: M16_Carbon_8_8,
            Calibration: null);

        var result = sut.Calculate(input);

        // 模型 A 会算成 ~300 kN（高估 ~3 倍）— 这就是 SimplifiedCalculator 类注释里讲的局限
        result.PreloadN!.Value.Should().BeInRange(280_000m, 330_000m,
            "模型 A 直接用真机 Δt + 标称 v 会高估几倍，这是已知物理局限");
    }

    [Fact]
    public void Calculate_TemperatureDelta_AppliesCompensation()
    {
        // 温度升高 5°C，按 SDK 系数 1.0269e-4：
        //   normalizedTof = echoTime / (1 + 1.0269e-4 × 5) = echoTime / 1.000513
        //   补偿后 deltaTof 应该接近 0（温度独自变化不应该产生预紧力）
        var sut = new SimplifiedCalculator();
        var input = new PreloadInput(
            // 模拟纯温度升 5°C 时 SDK 返回的 TOF（按相同的归一化）
            EchoTimeNs: 27200m * 1.000513m,
            RefTimeNs: 27200m,
            TempC: 27m,                       // +5°C
            RefTempC: 22m,
            TempCoefficient: 0.0001027m,
            Bolt: M16_Carbon_8_8,
            Calibration: null);

        var result = sut.Calculate(input);

        // 温补后 deltaTof 接近 0；容差 0.02 ns 是因为测试输入 EchoTimeNs 写到 6 位精度，
        // 而 SDK 系数 1.0269e-4 算到 decimal 精度更高，会有 ~0.01 ns 的截断差异
        result.DeltaTofNs.Should().BeApproximately(0m, 0.02m,
            "温补应该把纯温度效应抵消掉");
        result.PreloadN.Should().NotBeNull();
        Math.Abs(result.PreloadN!.Value).Should().BeLessThan(50m,
            "纯温度变化不应该产生明显预紧力（< 50 N，相对 80 kN 是 < 0.1%）");
    }

    [Fact]
    public void Calculate_StressInGbRange_FlagsWithinRange()
    {
        // 30%~90% × Rp0.2 = 192~576 MPa
        // 想要 σ = 400 MPa（在范围中段）
        // F = σ × A_e = 400 × 201 = 80,400 N
        // Δt = F × 2 × L₀ / (E × S × v) （SI 单位）
        //    = 80,400 × 2 × 0.080 / (210e9 × 201e-6 × 5890)
        //    ≈ 51.8 ns
        var sut = new SimplifiedCalculator();
        var input = new PreloadInput(
            EchoTimeNs: 27200m + 51.8m,
            RefTimeNs: 27200m,
            TempC: 22m,
            RefTempC: 22m,
            TempCoefficient: 0.0001027m,
            Bolt: M16_Carbon_8_8,
            Calibration: null);

        var result = sut.Calculate(input);

        result.WithinGbRange.Should().BeTrue("80 kN / 201 mm² ≈ 400 MPa 在 30%~90% × 640 = 192~576 范围内");
    }

    [Fact]
    public void Calculate_ZeroEffectiveLength_ReturnsNullInsteadOfThrowing()
    {
        // L_e = 0 在护栏前会触发 decimal 除零异常，中断 50Hz×N 通道的入库热路径。
        // 护栏后应安全返回 null（preloadN 无法计算），且不抛异常。
        var sut = new SimplifiedCalculator();
        var badBolt = M16_Carbon_8_8 with { EffectiveLengthMm = 0m };
        var input = new PreloadInput(
            EchoTimeNs: 27200m + 50m,
            RefTimeNs: 27200m,
            TempC: 22m,
            RefTempC: 22m,
            TempCoefficient: 0.0001027m,
            Bolt: badBolt,
            Calibration: null);

        var result = sut.Calculate(input);   // 护栏前：这里会抛 DivideByZeroException

        result.PreloadN.Should().BeNull();
        result.UncertaintyN.Should().BeNull();
        result.WithinGbRange.Should().BeFalse();
        result.ModelUsed.Should().Be("Simplified:invalid_bolt_spec");
    }

    [Fact]
    public void Calculate_StressBelowGbRange_FlagsOutOfRange()
    {
        // 极小 Δt → 应力远低于 30% × Rp0.2
        var sut = new SimplifiedCalculator();
        var input = new PreloadInput(
            EchoTimeNs: 27200m + 1m,    // Δt = 1 ns
            RefTimeNs: 27200m,
            TempC: 22m,
            RefTempC: 22m,
            TempCoefficient: 0.0001027m,
            Bolt: M16_Carbon_8_8,
            Calibration: null);

        var result = sut.Calculate(input);

        result.WithinGbRange.Should().BeFalse("应力远低于 30% × Rp0.2 = 192 MPa");
    }
}
