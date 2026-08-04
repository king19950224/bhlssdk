using BoltPreload.Algorithms.Preload;
using FluentAssertions;
using Xunit;

namespace BoltPreload.Tests.Algorithms;

/// <summary>
/// GbSingleWaveCalculator 单元测试：标定之后的正式预紧力换算（方案 §8.3 单波法）。
/// <para>
/// 这是全系统对外负责的那个数字 —— 报警、验收报告、大屏读数都取自它，而在补这批测试之前
/// 它一行直接测试都没有。此前唯一碰到它的是 AlarmPipelineIntegrationTests，但那里把样本温度
/// 设成参考温度，温补因子恒等于 1，温度系数写错十倍或符号写反都测不出来。
/// </para>
/// <para>
/// 因此这里刻意钉死三件容易悄悄改坏、且坏了不会报错只会让数偏的事：
/// K_L 的量纲方向（F = Δt/K_L，ns/N）、温补的方向与量级、以及负预紧力必须显式返回 null
/// 而不是取绝对值抹平（历史版本用过 Math.Abs，等于把标定方向反了这种事藏起来）。
/// </para>
/// </summary>
public class GbSingleWaveCalculatorTests
{
    /// <summary>典型 M16 × 80mm 碳钢 8.8 级。Rp0.2=640 MPa、A_e=201 mm² → GB 区间 192~576 MPa。</summary>
    private static BoltSpec M16_Carbon_8_8 => new(
        Grade: "8.8",
        Rp02Mpa: 640m,
        ElasticModulusGpa: 210m,
        NominalDiamMm: 16m,
        EffectiveLengthMm: 80m,
        CrossSectionMm2: 201m,
        SoundVelocityMs: 5890m);

    /// <summary>K_L = 0.001 ns/N，即 Δt 每多 1 ns 对应 1000 N，方便反推出整数预紧力。</summary>
    private static CalibrationData Calib(decimal kL = 0.001m, decimal kLUncertainty = 0m) => new(
        CalibrationId: Guid.Parse("11111111-1111-1111-1111-111111111111"),
        KL: kL,
        KLUncertainty: kLUncertainty,
        CalibratedAt: DateTimeOffset.UnixEpoch,
        ExpiresAt: DateTimeOffset.UnixEpoch.AddYears(100),
        Method: "lab_tensile");

    /// <summary>按目标 Δt 造一条输入，默认温度等于参考温度（温补中性）。</summary>
    private static PreloadInput Input(
        decimal deltaTofNs,
        CalibrationData? calibration,
        BoltSpec? bolt = null,
        decimal tempC = 22m,
        decimal refTempC = 22m,
        decimal tempCoefficient = 0.0001027m) => new(
            EchoTimeNs: 27200m + deltaTofNs,
            RefTimeNs: 27200m,
            TempC: tempC,
            RefTempC: refTempC,
            TempCoefficient: tempCoefficient,
            Bolt: bolt ?? M16_Carbon_8_8,
            Calibration: calibration);

    // ========== K_L 量纲方向 ==========

    [Fact]
    public void Calculate_UsesDeltaTofDividedByKl_NotMultiplied()
    {
        // 全系统裁定 K_L = Δt/F（ns/N），所以 F = Δt/K_L。
        // Δt=100 ns、K_L=0.001 ns/N → 100 kN。若哪天有人翻成 F = Δt·K_L，这里会变成 0.1 N。
        var sut = new GbSingleWaveCalculator();

        var result = sut.Calculate(Input(deltaTofNs: 100m, Calib()));

        result.PreloadN.Should().NotBeNull();
        result.PreloadN!.Value.Should().BeApproximately(100_000m, 0.01m);
        result.DeltaTofNs.Should().BeApproximately(100m, 0.001m);
        result.ModelUsed.Should().Be("GbSingleWave");
        result.CalibrationId.Should().Be(Calib().CalibrationId);
    }

    [Fact]
    public void Calculate_ScalesLinearlyWithDeltaTof()
    {
        // 单波法是线性模型，Δt 翻倍预紧力就该翻倍。防的是有人往里塞非线性修正却没改文档。
        var sut = new GbSingleWaveCalculator();

        var single = sut.Calculate(Input(50m, Calib())).PreloadN!.Value;
        var doubled = sut.Calculate(Input(100m, Calib())).PreloadN!.Value;

        doubled.Should().BeApproximately(single * 2m, 0.01m);
    }

    // ========== 温度补偿 ==========

    [Fact]
    public void Calculate_PureTemperatureRise_CompensatesToZeroPreload()
    {
        // 只升温、不加载：SDK 读到的 TOF 会按 (1 + α·ΔT) 变长，温补必须把它抵消回去。
        // α=1.0269e-4、ΔT=+10°C → tempFactor=1.0010269，原始回波 = 27200 × 1.0010269。
        // 补偿正确则 Δt≈0、预紧力≈0；系数写错十倍会得到约 25 万 N 的凭空预紧力。
        var sut = new GbSingleWaveCalculator();
        const decimal alpha = 0.0001027m;
        var rawEcho = 27200m * (1m + alpha * 10m);

        var result = sut.Calculate(new PreloadInput(
            EchoTimeNs: rawEcho,
            RefTimeNs: 27200m,
            TempC: 32m,
            RefTempC: 22m,
            TempCoefficient: alpha,
            Bolt: M16_Carbon_8_8,
            Calibration: Calib()));

        result.DeltaTofNs.Should().BeApproximately(0m, 0.02m, "纯温度效应必须被温补吃掉");
        Math.Abs(result.PreloadN!.Value).Should().BeLessThan(50m,
            "温度独自变化不该产生预紧力（<50 N，相对 100 kN 量级可忽略）");
    }

    [Fact]
    public void Calculate_TemperatureFall_CompensatesInOppositeDirection()
    {
        // 降温方向同样要成立。只测升温的话，符号写反在升温样本上仍可能凑巧接近 0。
        var sut = new GbSingleWaveCalculator();
        const decimal alpha = 0.0001027m;
        var rawEcho = 27200m * (1m + alpha * -10m);

        var result = sut.Calculate(new PreloadInput(
            EchoTimeNs: rawEcho,
            RefTimeNs: 27200m,
            TempC: 12m,
            RefTempC: 22m,
            TempCoefficient: alpha,
            Bolt: M16_Carbon_8_8,
            Calibration: Calib()));

        result.DeltaTofNs.Should().BeApproximately(0m, 0.02m);
    }

    [Fact]
    public void Calculate_NonPhysicalTempFactor_ReturnsNullWithoutThrowing()
    {
        // tempFactor ≤ 0 时归一化没有物理意义，且会把 Δt 的符号翻过来。
        // 这里用 α=0.01、ΔT=-100 凑出 tempFactor 恰好为 0：护栏前是除零，护栏后应安全返回 null。
        var sut = new GbSingleWaveCalculator();

        var result = sut.Calculate(Input(
            deltaTofNs: 100m, Calib(), tempC: -78m, refTempC: 22m, tempCoefficient: 0.01m));

        result.PreloadN.Should().BeNull();
        result.UncertaintyN.Should().BeNull();
        result.DeltaTofNs.Should().Be(0m);
        result.WithinGbRange.Should().BeFalse();
    }

    // ========== 护栏 ==========

    [Fact]
    public void Calculate_NoCalibration_ReturnsAllNull()
    {
        // 没有标定就不该给出"正式"预紧力。上层据此降级到模型 A。
        var sut = new GbSingleWaveCalculator();

        var result = sut.Calculate(Input(100m, calibration: null));

        result.PreloadN.Should().BeNull();
        result.UncertaintyN.Should().BeNull();
        result.CalibrationId.Should().BeNull();
        result.WithinGbRange.Should().BeFalse();
    }

    [Fact]
    public void Calculate_NegativePreload_ReturnsNullAndTagsModel()
    {
        // 螺栓比安装时还短 → 多半是标定方向反了或基线漂了。历史版本 Math.Abs 抹平，
        // 等于把错误数据当成正常预紧力上报。现在必须显式 null，并在 ModelUsed 里留下线索。
        var sut = new GbSingleWaveCalculator();

        var result = sut.Calculate(Input(deltaTofNs: -100m, Calib()));

        result.PreloadN.Should().BeNull();
        result.UncertaintyN.Should().BeNull();
        result.WithinGbRange.Should().BeFalse();
        result.ModelUsed.Should().Be("GbSingleWave:negative_preload");
        result.DeltaTofNs.Should().BeApproximately(-100m, 0.001m, "Δt 本身要保留，供排查用");
    }

    [Fact]
    public void Calculate_NonPositiveKl_ReturnsNullButKeepsDeltaTof()
    {
        var sut = new GbSingleWaveCalculator();

        var result = sut.Calculate(Input(100m, Calib(kL: 0m)));

        result.PreloadN.Should().BeNull();
        result.DeltaTofNs.Should().BeApproximately(100m, 0.001m);
    }

    [Fact]
    public void Calculate_ZeroCrossSection_DoesNotThrowAndFlagsOutOfRange()
    {
        // A_e=0 会让应力计算除零，中断 50Hz×N 通道的入库热路径。
        var sut = new GbSingleWaveCalculator();
        var badBolt = M16_Carbon_8_8 with { CrossSectionMm2 = 0m };

        var result = sut.Calculate(Input(100m, Calib(), bolt: badBolt));

        result.PreloadN.Should().NotBeNull("预紧力本身不依赖截面积");
        result.WithinGbRange.Should().BeFalse("算不出应力就不能声称在 GB 范围内");
    }

    // ========== GB 适用范围 30%~90% Rp0.2 ==========

    // A_e=201 mm²、Rp0.2=640 MPa → 下沿 192 MPa = 38592 N、上沿 576 MPa = 115776 N。
    // K_L=0.001 时 Δt(ns) 与 F(N) 是千分之一的关系，所以 Δt 直接取 F/1000。
    [Theory]
    [InlineData(38.592, true, "恰好 30% 下沿，判定用 >= 应当纳入")]
    [InlineData(38.591, false, "差 1 N 落在下沿之外")]
    [InlineData(80.400, true, "400 MPa，区间中段")]
    [InlineData(115.776, true, "恰好 90% 上沿，判定用 <= 应当纳入")]
    [InlineData(115.777, false, "差 1 N 落在上沿之外")]
    [InlineData(150.000, false, "远超上沿")]
    public void Calculate_GbRangeBoundaries_AreInclusive(decimal deltaTofNs, bool expected, string why)
    {
        // 边界用 >= / <= 还是 > / <，差一个等号就会让恰好压线的螺栓在"合规"与"超范围"之间跳变，
        // 而验收报告直接引用这个标志。
        var sut = new GbSingleWaveCalculator();

        var result = sut.Calculate(Input(deltaTofNs, Calib()));

        result.WithinGbRange.Should().Be(expected, why);
    }

    // ========== 不确定度 ==========

    [Fact]
    public void Calculate_CombinesDeltaTofAndKlUncertainty()
    {
        // u(F)/F = sqrt( (u(Δt)/Δt)² + (u(K_L)/K_L)² )，u(Δt) 取 1 ns，K_L 的 U(k=2) 先除 2 还原成 u。
        // Δt=100、K_L=0.001、U(K_L)=0.0001：
        //   relU_dt = 1/100 = 0.01；relU_kl = 0.0001/0.001/2 = 0.05
        //   combined = sqrt(0.0001 + 0.0025) ≈ 0.050990；U(F) = 100000 × 0.050990 × 2 ≈ 10198 N
        var sut = new GbSingleWaveCalculator();

        var result = sut.Calculate(Input(100m, Calib(kLUncertainty: 0.0001m)));

        result.UncertaintyN.Should().NotBeNull();
        result.UncertaintyN!.Value.Should().BeApproximately(10_198m, 1m);
    }

    [Fact]
    public void Calculate_WithoutKlUncertainty_FallsBackToDeltaTofNoiseOnly()
    {
        // 标定记录没填 U 时只剩 1 ns 噪声项：100000 × 0.01 × 2 = 2000 N。
        var sut = new GbSingleWaveCalculator();

        var result = sut.Calculate(Input(100m, Calib(kLUncertainty: 0m)));

        result.UncertaintyN!.Value.Should().BeApproximately(2_000m, 0.5m);
    }

    [Fact]
    public void Calculate_DeltaTofWithinNoiseFloor_ReportsNoUncertainty()
    {
        // |Δt| ≤ 1 ns 时信号淹没在噪声里，相对不确定度会发散，此时不给不确定度而不是给个天文数字。
        var sut = new GbSingleWaveCalculator();

        var result = sut.Calculate(Input(1m, Calib()));

        result.PreloadN!.Value.Should().BeApproximately(1_000m, 0.01m);
        result.UncertaintyN.Should().BeNull();
    }

    [Fact]
    public void Metadata_DeclaresGbSingleWaveAccuracy()
    {
        // sink 按 AccuracyLevel 决定这条数据算不算"正式监测值"，写错等级会让模型 A 的估算冒充正式值。
        var sut = new GbSingleWaveCalculator();

        sut.ModelName.Should().Be("GbSingleWave");
        sut.AccuracyLevel.Should().Be(PreloadAccuracyLevel.GbSingleWave);
    }
}
