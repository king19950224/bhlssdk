using BoltPreload.Domain;
using FluentAssertions;
using Xunit;

namespace BoltPreload.Tests.Domain;

public class BoltStandardsTests
{
    [Theory]
    [InlineData("8.8", 16, 640)]     // 8.8 分档边界：d≤16 → 640
    [InlineData("8.8", 20, 660)]     // d>16 → 660
    [InlineData("10.9", 36, 940)]
    [InlineData("12.9", 20, 1080)]
    [InlineData("A2-70", 12, 450)]
    [InlineData("a2-70", 12, 450)]   // 大小写不敏感
    public void Rp02_KnownGrades(string grade, decimal diam, decimal expected)
        => BoltStandards.Rp02Mpa(grade, diam).Should().Be(expected);

    [Fact]
    public void Rp02_UnknownGrade_ReturnsNull_NotAGuess()
        => BoltStandards.Rp02Mpa("7.7", 20).Should().BeNull("未收录等级必须让人显式填，不能猜");

    [Theory]
    [InlineData(20, 245)]
    [InlineData(36, 817)]
    [InlineData(42, 1120)]
    public void StressArea_KnownThreads(decimal diam, decimal expected)
        => BoltStandards.StressAreaMm2(diam).Should().Be(expected);

    [Fact]
    public void StressArea_UnknownThread_ReturnsNull()
        => BoltStandards.StressAreaMm2(21).Should().BeNull();

    [Theory]
    [InlineData("M20", 20)]
    [InlineData(" m36 ", 36)]
    [InlineData("42", 42)]
    public void ParseNominalDiam_CommonForms(string input, decimal expected)
        => BoltStandards.ParseNominalDiamMm(input).Should().Be(expected);

    [Fact]
    public void ParseNominalDiam_Garbage_ReturnsNull()
        => BoltStandards.ParseNominalDiamMm("Mxx").Should().BeNull();
}
