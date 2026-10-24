using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Kasi.App.Controls;

/// <summary>
/// A small area chart of the last few samples, newest at the right (the aggregate-speed card, the per-download bandwidth chart). One drawn
/// control, so a chart costs one render, not a control per point.
/// </summary>
internal sealed class Sparkline : Control
{
    public static readonly StyledProperty<IReadOnlyList<double>?> SamplesProperty =
        AvaloniaProperty.Register<Sparkline, IReadOnlyList<double>?>(nameof(Samples));

    public static readonly StyledProperty<int> CapacityProperty = AvaloniaProperty.Register<Sparkline, int>(nameof(Capacity), 60);

    public static readonly StyledProperty<double?> MaximumProperty = AvaloniaProperty.Register<Sparkline, double?>(nameof(Maximum));

    public static readonly StyledProperty<IBrush?> LineBrushProperty = AvaloniaProperty.Register<Sparkline, IBrush?>(nameof(LineBrush));

    static Sparkline() => AffectsRender<Sparkline>(SamplesProperty, CapacityProperty, MaximumProperty, LineBrushProperty);

    /// <summary>The samples to draw, oldest first; the view model replaces the list when a new sample arrives.</summary>
    public IReadOnlyList<double>? Samples
    {
        get => GetValue(SamplesProperty);
        set => SetValue(SamplesProperty, value);
    }

    /// <summary>How many samples fill the width (60 for a minute of one-second samples).</summary>
    public int Capacity
    {
        get => GetValue(CapacityProperty);
        set => SetValue(CapacityProperty, value);
    }

    /// <summary>The value at the top of the chart; null scales to the largest sample.</summary>
    public double? Maximum
    {
        get => GetValue(MaximumProperty);
        set => SetValue(MaximumProperty, value);
    }

    public IBrush? LineBrush
    {
        get => GetValue(LineBrushProperty);
        set => SetValue(LineBrushProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize) =>
        new(double.IsInfinity(availableSize.Width) ? 200 : availableSize.Width, double.IsInfinity(availableSize.Height) ? 48 : availableSize.Height);

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        var width = Bounds.Width;
        var height = Bounds.Height;
        var points = SparklineGeometry.Points(Samples ?? [], width, height, Capacity, Maximum);
        if (points.Count < 2)
        {
            return;
        }

        var line = LineBrush ?? Brushes.Gray;
        var color = (line as ISolidColorBrush)?.Color ?? Colors.Gray;
        var area = new StreamGeometry();
        using (var geometry = area.Open())
        {
            geometry.BeginFigure(new Point(points[0].X, height), isFilled: true);
            foreach (var point in points)
            {
                geometry.LineTo(point);
            }

            geometry.LineTo(new Point(points[^1].X, height));
            geometry.EndFigure(isClosed: true);
        }

        var fill = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
            GradientStops = [new GradientStop(Color.FromArgb(0x55, color.R, color.G, color.B), 0), new GradientStop(Color.FromArgb(0x00, color.R, color.G, color.B), 1)],
        };
        context.DrawGeometry(fill, null, area);

        var path = new StreamGeometry();
        using (var geometry = path.Open())
        {
            geometry.BeginFigure(points[0], isFilled: false);
            foreach (var point in points.Skip(1))
            {
                geometry.LineTo(point);
            }

            geometry.EndFigure(isClosed: false);
        }

        context.DrawGeometry(null, new Pen(line, 2, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round), path);
    }
}
