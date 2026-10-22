using SmartShift.Core.Actions;
using Xunit;

namespace SmartShift.Core.Tests;

public sealed class RadialLayoutCalculatorTests
{
    [Fact]
    public void CalculateAngle_DistributesFiveItemsStartingAtTop()
    {
        var angle0 = RadialLayoutCalculator.CalculateAngle(0, 5);
        var angle1 = RadialLayoutCalculator.CalculateAngle(1, 5);
        var angle2 = RadialLayoutCalculator.CalculateAngle(2, 5);
        var angle3 = RadialLayoutCalculator.CalculateAngle(3, 5);
        var angle4 = RadialLayoutCalculator.CalculateAngle(4, 5);

        Assert.Equal(-90.0, angle0, 3);
        Assert.Equal(-18.0, angle1, 3);
        Assert.Equal(54.0, angle2, 3);
        Assert.Equal(126.0, angle3, 3);
        Assert.Equal(198.0, angle4, 3);
    }

    [Fact]
    public void CalculateAngle_DistributesThreeItemsEqually()
    {
        var angle0 = RadialLayoutCalculator.CalculateAngle(0, 3);
        var angle1 = RadialLayoutCalculator.CalculateAngle(1, 3);
        var angle2 = RadialLayoutCalculator.CalculateAngle(2, 3);

        Assert.Equal(-90.0, angle0, 3);
        Assert.Equal(30.0, angle1, 3);
        Assert.Equal(150.0, angle2, 3);
    }

    [Fact]
    public void CalculateAngle_HandlesZeroCountSafely()
    {
        var angle = RadialLayoutCalculator.CalculateAngle(0, 0);
        Assert.Equal(-90.0, angle);
    }

    [Fact]
    public void CalculateRadius_ClampsBetweenMinAndMax()
    {
        // Very small dimensions
        var smallRadius = RadialLayoutCalculator.CalculateRadius(100.0, 100.0);
        Assert.Equal(RadialLayoutCalculator.MinRadius, smallRadius);

        // Very large dimensions
        var largeRadius = RadialLayoutCalculator.CalculateRadius(1000.0, 1000.0);
        Assert.Equal(RadialLayoutCalculator.MaxRadius, largeRadius);

        // Mid-range calculation: minDim = 400 -> (400 / 2) - 52 = 148
        var midRadius = RadialLayoutCalculator.CalculateRadius(400.0, 500.0);
        Assert.Equal(148.0, midRadius, 3);
    }

    [Fact]
    public void CalculateCenterSize_ClampsBetweenMinAndMax()
    {
        // Very small dimensions
        var smallCenter = RadialLayoutCalculator.CalculateCenterSize(100.0, 100.0);
        Assert.Equal(RadialLayoutCalculator.MinCenterSize, smallCenter);

        // Very large dimensions
        var largeCenter = RadialLayoutCalculator.CalculateCenterSize(1000.0, 1000.0);
        Assert.Equal(RadialLayoutCalculator.MaxCenterSize, largeCenter);

        // Mid-range: minDim = 400 -> 400 * 0.44 = 176
        var midCenter = RadialLayoutCalculator.CalculateCenterSize(400.0, 600.0);
        Assert.Equal(176.0, midCenter, 3);
    }

    [Fact]
    public void CalculateItemPosition_PlacesItemCenteredOnOrbitalRing()
    {
        double centerX = 250.0;
        double centerY = 250.0;
        double radius = 100.0;
        double itemW = 50.0;
        double itemH = 50.0;

        // Angle -90 degrees is straight up: (centerX, centerY - radius)
        var (xTop, yTop) = RadialLayoutCalculator.CalculateItemPosition(centerX, centerY, radius, -90.0, itemW, itemH);
        Assert.Equal(225.0, xTop, 3); // 250 + 0 - 25
        Assert.Equal(125.0, yTop, 3); // 250 - 100 - 25

        // Angle 0 degrees is straight right: (centerX + radius, centerY)
        var (xRight, yRight) = RadialLayoutCalculator.CalculateItemPosition(centerX, centerY, radius, 0.0, itemW, itemH);
        Assert.Equal(325.0, xRight, 3); // 250 + 100 - 25
        Assert.Equal(225.0, yRight, 3); // 250 + 0 - 25
    }
}
