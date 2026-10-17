using GlassRea.Core.Alerts;
using GlassRea.Core.Look;
using GlassRea.Core.Usage;

namespace GlassRea.Tests;

public class CycleTests
{
    private static DateTime D(int y, int m, int d) => new(y, m, d);

    [Fact]
    public void MonthStartsOnTheFirstByDefault()
    {
        Assert.Equal((D(2026, 10, 1), D(2026, 11, 1)), Cycle.Month(D(2026, 10, 17), 1));
    }

    [Fact]
    public void MonthRunsFromTheResetDayToTheNext()
    {
        Assert.Equal((D(2026, 10, 15), D(2026, 11, 15)), Cycle.Month(D(2026, 10, 17), 15));
        // Before this month's reset day, the period began last month.
        Assert.Equal((D(2026, 9, 15), D(2026, 10, 15)), Cycle.Month(D(2026, 10, 14), 15));
        Assert.Equal((D(2026, 10, 15), D(2026, 11, 15)), Cycle.Month(D(2026, 10, 15), 15));
    }

    [Fact]
    public void LateResetDaysFallOnTheLastDayOfShortMonths()
    {
        Assert.Equal((D(2027, 1, 31), D(2027, 2, 28)), Cycle.Month(D(2027, 2, 10), 31));
        Assert.Equal((D(2027, 2, 28), D(2027, 3, 31)), Cycle.Month(D(2027, 3, 1), 31));
        Assert.Equal((D(2028, 2, 29), D(2028, 3, 30)), Cycle.Month(D(2028, 3, 5), 30)); // leap year
    }

    [Fact]
    public void YearBoundary()
    {
        Assert.Equal((D(2026, 12, 20), D(2027, 1, 20)), Cycle.Month(D(2027, 1, 3), 20));
    }

    [Theory]
    [InlineData(DayOfWeek.Saturday, 17)] // 17 Oct 2026 is a Saturday
    [InlineData(DayOfWeek.Sunday, 11)]
    [InlineData(DayOfWeek.Monday, 12)]
    public void WeekStartsOnTheChosenDay(DayOfWeek first, int startDay)
    {
        var (start, end) = Cycle.Week(D(2026, 10, 17), first);
        Assert.Equal(D(2026, 10, startDay), start);
        Assert.Equal(start.AddDays(7), end);
    }

    [Theory]
    [InlineData("15", 15)]
    [InlineData("31", 31)]
    [InlineData("0", 1)]
    [InlineData("32", 1)]
    [InlineData("\u0661\u0665", 1)] // Arabic-Indic digits are not accepted as a stored value
    [InlineData(null, 1)]
    public void ParsesTheMonthDaySetting(string? text, int expected) => Assert.Equal(expected, Cycle.ParseMonthDay(text));

    [Fact]
    public void ParsesTheWeekStartSetting()
    {
        Assert.Equal(DayOfWeek.Monday, Cycle.ParseWeekStart("monday"));
        Assert.Equal(DayOfWeek.Saturday, Cycle.ParseWeekStart("tuesday"));
        Assert.True(Cycle.IsValidWeekStart("sunday"));
        Assert.False(Cycle.IsValidWeekStart("friday"));
    }

    [Theory]
    [InlineData(1, "1st")]
    [InlineData(2, "2nd")]
    [InlineData(3, "3rd")]
    [InlineData(11, "11th")]
    [InlineData(12, "12th")]
    [InlineData(13, "13th")]
    [InlineData(21, "21st")]
    [InlineData(22, "22nd")]
    [InlineData(31, "31st")]
    public void Ordinals(int day, string text) => Assert.Equal(text, Cycle.Ordinal(day));

    [Fact]
    public void MonthlyLimitTextNamesTheResetDay()
    {
        Assert.EndsWith("The count starts again on the 15th.", AlertText.Limit("Monthly", 80, 8, 10, 15).Body);
        Assert.EndsWith("The count starts again on the 31st, or the last day of a shorter month.", AlertText.Limit("Monthly", 100, 11, 10, 31).Body);
    }
}

public class LiquidGlassTests
{
    [Fact]
    public void SquircleProfileRisesSteeplyAndFlattens()
    {
        Assert.Equal(0, LiquidGlass.Height(0));
        Assert.Equal(1, LiquidGlass.Height(1));
        Assert.Equal(0.766, LiquidGlass.Height(0.1), 3);
        Assert.Equal(0.934, LiquidGlass.Height(0.3), 3);
        Assert.Equal(0.984, LiquidGlass.Height(0.5), 3);
        Assert.Equal(0, LiquidGlass.Slope(1));
    }

    [Fact]
    public void SnellsLawWithCrownGlass()
    {
        // sin(θt) = sin(θi) / 1.5
        Assert.Equal(Math.Asin(Math.Sin(0.7) / 1.5), LiquidGlass.Refracted(0.7), 12);
        Assert.Equal(0.04, LiquidGlass.F0, 6);
        Assert.Equal(0.04, LiquidGlass.Fresnel(1), 6);  // straight on
        Assert.Equal(1, LiquidGlass.Fresnel(0), 6);     // grazing
    }

    [Fact]
    public void RefractionShiftMatchesTheWorkedNumbers()
    {
        // B = T = 12, slab 6 (see the summary on LiquidGlass).
        Assert.Equal(7.34, LiquidGlass.Shift(1, 12), 2);
        Assert.Equal(4.69, LiquidGlass.Shift(2, 12), 2);
        Assert.Equal(1.27, LiquidGlass.Shift(5, 12), 2);
        Assert.True(LiquidGlass.Shift(9, 12) < 0.1);
        Assert.Equal(0, LiquidGlass.Shift(12, 12));
    }

    [Fact]
    public void ShiftFadesInwardsAcrossTheBezel()
    {
        var last = double.MaxValue;
        for (var d = 0.5; d <= 12; d += 0.5)
        {
            var s = LiquidGlass.Shift(d, 12);
            Assert.True(s <= last, $"shift grew at d={d}");
            last = s;
        }
    }

    [Fact]
    public void RimIsBrightTopLeftWithAWeakerGlintBottomRight()
    {
        double Peak(double nx, double ny) => Enumerable.Range(1, 40).Max(i => LiquidGlass.Rim(nx, ny, i * 0.1, 12, 1));
        var top = Peak(0, -1);
        var bottom = Peak(0, 1);
        var left = Peak(-1, 0);
        Assert.True(top > bottom && bottom > 0, $"top {top}, bottom {bottom}");
        Assert.True(top > left, $"top {top}, left {left}");
        Assert.Equal(0, LiquidGlass.Rim(0, -1, 12, 12, 1)); // flat glass reflects nothing extra
    }
}
