using System;
using System.Windows;
using System.Windows.Media;

namespace SmartShift.App.Ui;

public static class PetalWheelGeometry
{
    public static PathGeometry CreatePetal(
        double cx, double cy,
        double innerRadius, double outerRadius,
        double startAngleDeg, double endAngleDeg)
    {
        var geom = new PathGeometry();
        var figure = new PathFigure();

        double radStart = startAngleDeg * (Math.PI / 180.0);
        double radEnd = endAngleDeg * (Math.PI / 180.0);

        Point pInnerStart = new(cx + innerRadius * Math.Cos(radStart), cy + innerRadius * Math.Sin(radStart));
        Point pOuterStart = new(cx + outerRadius * Math.Cos(radStart), cy + outerRadius * Math.Sin(radStart));
        Point pOuterEnd = new(cx + outerRadius * Math.Cos(radEnd), cy + outerRadius * Math.Sin(radEnd));
        Point pInnerEnd = new(cx + innerRadius * Math.Cos(radEnd), cy + innerRadius * Math.Sin(radEnd));

        figure.StartPoint = pInnerStart;
        figure.Segments.Add(new LineSegment(pOuterStart, isStroked: true));
        figure.Segments.Add(new ArcSegment(
            pOuterEnd,
            new Size(outerRadius, outerRadius),
            rotationAngle: 0,
            isLargeArc: (endAngleDeg - startAngleDeg) > 180,
            sweepDirection: SweepDirection.Clockwise,
            isStroked: true));
        figure.Segments.Add(new LineSegment(pInnerEnd, isStroked: true));
        figure.Segments.Add(new ArcSegment(
            pInnerStart,
            new Size(innerRadius, innerRadius),
            rotationAngle: 0,
            isLargeArc: (endAngleDeg - startAngleDeg) > 180,
            sweepDirection: SweepDirection.Counterclockwise,
            isStroked: true));

        figure.IsClosed = true;
        figure.IsFilled = true;
        geom.Figures.Add(figure);
        geom.Freeze();
        return geom;
    }
}
