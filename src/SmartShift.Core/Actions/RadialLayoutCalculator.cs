namespace SmartShift.Core.Actions;

public static class RadialLayoutCalculator
{
    public const double MinRadius = 135.0;
    public const double MaxRadius = 215.0;
    public const double MinCenterSize = 160.0;
    public const double MaxCenterSize = 220.0;

    public static double CalculateAngle(int index, int totalCount)
    {
        if (totalCount <= 0)
        {
            return -90.0;
        }

        return -90.0 + (index * (360.0 / totalCount));
    }

    public static double CalculateRadius(double width, double height)
    {
        var minDim = Math.Min(width, height);
        return Math.Clamp((minDim / 2.0) - 52.0, MinRadius, MaxRadius);
    }

    public static double CalculateCenterSize(double width, double height)
    {
        var minDim = Math.Min(width, height);
        return Math.Clamp(minDim * 0.44, MinCenterSize, MaxCenterSize);
    }

    public static (double X, double Y) CalculateItemPosition(
        double centerX,
        double centerY,
        double radius,
        double angleDegrees,
        double itemWidth,
        double itemHeight)
    {
        var rad = angleDegrees * (Math.PI / 180.0);
        var x = centerX + (radius * Math.Cos(rad)) - (itemWidth / 2.0);
        var y = centerY + (radius * Math.Sin(rad)) - (itemHeight / 2.0);
        return (x, y);
    }
}
