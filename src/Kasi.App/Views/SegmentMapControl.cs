using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Kasi.Core;

namespace Kasi.App.Views;

/// <summary>
/// The segment map: one block per segment, as wide as the segment is long, filled as far as its bytes have been written. One control
/// that draws itself, so a download split into hundreds of segments costs one render, not hundreds of controls.
/// </summary>
internal sealed class SegmentMapControl : Control
{
    public static readonly StyledProperty<IReadOnlyList<Segment>?> SegmentsProperty =
        AvaloniaProperty.Register<SegmentMapControl, IReadOnlyList<Segment>?>(nameof(Segments));

    public static readonly StyledProperty<IBrush?> FillBrushProperty =
        AvaloniaProperty.Register<SegmentMapControl, IBrush?>(nameof(FillBrush));

    public static readonly StyledProperty<IBrush?> TrackBrushProperty =
        AvaloniaProperty.Register<SegmentMapControl, IBrush?>(nameof(TrackBrush));

    static SegmentMapControl()
    {
        AffectsRender<SegmentMapControl>(SegmentsProperty, FillBrushProperty, TrackBrushProperty);
        AffectsMeasure<SegmentMapControl>(SegmentsProperty);
    }

    public IReadOnlyList<Segment>? Segments
    {
        get => GetValue(SegmentsProperty);
        set => SetValue(SegmentsProperty, value);
    }

    public IBrush? FillBrush
    {
        get => GetValue(FillBrushProperty);
        set => SetValue(FillBrushProperty, value);
    }

    public IBrush? TrackBrush
    {
        get => GetValue(TrackBrushProperty);
        set => SetValue(TrackBrushProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize) => new(double.IsInfinity(availableSize.Width) ? 400 : availableSize.Width, 28);

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        var segments = Segments;
        var width = Bounds.Width;
        var height = Bounds.Height;
        if (segments is not { Count: > 0 } || width <= 0)
        {
            context.DrawRectangle(TrackBrush, null, new Rect(0, 0, width, height), 4, 4);
            return;
        }

        var total = (double)segments.Sum(s => s.Length);
        if (total <= 0)
        {
            return;
        }

        const double Gap = 1;
        foreach (var segment in segments)
        {
            var x = segment.Start / total * width;
            var w = Math.Max(1, (segment.Length / total * width) - Gap);
            context.DrawRectangle(TrackBrush, null, new Rect(x, 0, w, height), 2, 2);
            if (segment.Written > 0)
            {
                context.DrawRectangle(FillBrush, null, new Rect(x, 0, Math.Max(1, w * segment.Written / segment.Length), height), 2, 2);
            }
        }
    }
}
