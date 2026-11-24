using System.Windows;
using System.Windows.Media;

namespace OnTimeShutdown.Controls;

/// <summary>
/// A lightweight circular progress indicator that starts at 12 o'clock.
/// </summary>
public sealed class ProgressRing : FrameworkElement
{
    public static readonly DependencyProperty ProgressProperty = DependencyProperty.Register(
        nameof(Progress), typeof(double), typeof(ProgressRing),
        new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender, null, CoerceProgress));

    public static readonly DependencyProperty RingBrushProperty = DependencyProperty.Register(
        nameof(RingBrush), typeof(Brush), typeof(ProgressRing),
        new FrameworkPropertyMetadata(Brushes.Orange, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty TrackBrushProperty = DependencyProperty.Register(
        nameof(TrackBrush), typeof(Brush), typeof(ProgressRing),
        new FrameworkPropertyMetadata(new SolidColorBrush(Color.FromArgb(32, 255, 255, 255)), FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty StrokeThicknessProperty = DependencyProperty.Register(
        nameof(StrokeThickness), typeof(double), typeof(ProgressRing),
        new FrameworkPropertyMetadata(8d, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

    public double Progress
    {
        get => (double)GetValue(ProgressProperty);
        set => SetValue(ProgressProperty, value);
    }

    public Brush RingBrush
    {
        get => (Brush)GetValue(RingBrushProperty);
        set => SetValue(RingBrushProperty, value);
    }

    public Brush TrackBrush
    {
        get => (Brush)GetValue(TrackBrushProperty);
        set => SetValue(TrackBrushProperty, value);
    }

    public double StrokeThickness
    {
        get => (double)GetValue(StrokeThicknessProperty);
        set => SetValue(StrokeThicknessProperty, value);
    }

    private static object CoerceProgress(DependencyObject d, object baseValue)
    {
        return Math.Clamp((double)baseValue, 0d, 1d);
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);

        double side = Math.Min(ActualWidth, ActualHeight);
        if (side <= StrokeThickness * 2)
        {
            return;
        }

        var center = new Point(ActualWidth / 2d, ActualHeight / 2d);
        double radius = (side - StrokeThickness) / 2d;
        var trackPen = CreatePen(TrackBrush, StrokeThickness);
        drawingContext.DrawEllipse(null, trackPen, center, radius, radius);

        if (Progress <= 0.001)
        {
            return;
        }

        var ringPen = CreatePen(RingBrush, StrokeThickness);
        if (Progress >= 0.9995)
        {
            drawingContext.DrawEllipse(null, ringPen, center, radius, radius);
            return;
        }

        double angle = Progress * Math.PI * 2d;
        var startPoint = new Point(center.X, center.Y - radius);
        var endPoint = new Point(
            center.X + Math.Sin(angle) * radius,
            center.Y - Math.Cos(angle) * radius);

        var geometry = new StreamGeometry();
        using (StreamGeometryContext context = geometry.Open())
        {
            context.BeginFigure(startPoint, isFilled: false, isClosed: false);
            context.ArcTo(
                endPoint,
                new Size(radius, radius),
                rotationAngle: 0,
                isLargeArc: angle > Math.PI,
                sweepDirection: SweepDirection.Clockwise,
                isStroked: true,
                isSmoothJoin: false);
        }

        geometry.Freeze();
        drawingContext.DrawGeometry(null, ringPen, geometry);

        var capBrush = RingBrush.Clone();
        capBrush.Freeze();
        drawingContext.DrawEllipse(capBrush, null, endPoint, StrokeThickness / 2d, StrokeThickness / 2d);
    }

    private static Pen CreatePen(Brush brush, double thickness)
    {
        var pen = new Pen(brush, thickness)
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round
        };
        pen.Freeze();
        return pen;
    }
}
