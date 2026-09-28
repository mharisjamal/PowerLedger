using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;

namespace PowerLedger.App.Aero;

/// <summary>
/// One of Aero's icons (Icons.Aero.xaml, the HTML's own, on a 24 unit grid), drawn at <see cref="Size"/> in the inherited
/// foreground: stroked with round caps as the HTML's SVGs are, or filled. Decorative: it takes no pointer or keyboard, and
/// the control it sits in carries the name a screen reader says.
/// </summary>
public sealed class Icon : FrameworkElement
{
    public static readonly DependencyProperty DataProperty = DependencyProperty.Register(nameof(Data), typeof(System.Windows.Media.Geometry),
        typeof(Icon), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty SizeProperty = DependencyProperty.Register(nameof(Size), typeof(double),
        typeof(Icon), new FrameworkPropertyMetadata(16.0, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty FilledProperty = DependencyProperty.Register(nameof(Filled), typeof(bool),
        typeof(Icon), new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty StrokeWidthProperty = DependencyProperty.Register(nameof(StrokeWidth), typeof(double),
        typeof(Icon), new FrameworkPropertyMetadata(2.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ForegroundProperty = TextElement.ForegroundProperty.AddOwner(typeof(Icon),
        new FrameworkPropertyMetadata(Brushes.White, FrameworkPropertyMetadataOptions.Inherits | FrameworkPropertyMetadataOptions.AffectsRender));

    public Icon()
    {
        IsHitTestVisible = false;
        Focusable = false;
        SnapsToDevicePixels = false;
        HorizontalAlignment = HorizontalAlignment.Center;
        VerticalAlignment = VerticalAlignment.Center;
    }

    public System.Windows.Media.Geometry? Data { get => (System.Windows.Media.Geometry?)GetValue(DataProperty); set => SetValue(DataProperty, value); }

    public double Size { get => (double)GetValue(SizeProperty); set => SetValue(SizeProperty, value); }

    public bool Filled { get => (bool)GetValue(FilledProperty); set => SetValue(FilledProperty, value); }

    public double StrokeWidth { get => (double)GetValue(StrokeWidthProperty); set => SetValue(StrokeWidthProperty, value); }

    public Brush Foreground { get => (Brush)GetValue(ForegroundProperty); set => SetValue(ForegroundProperty, value); }

    protected override Size MeasureOverride(Size availableSize) => new(Size, Size);

    protected override void OnRender(DrawingContext drawingContext)
    {
        if (Data is null) return;
        var k = Size / 24;
        drawingContext.PushTransform(new ScaleTransform(k, k));
        if (Filled)
        {
            drawingContext.DrawGeometry(Foreground, null, Data);
        }
        else
        {
            var pen = new Pen(Foreground, StrokeWidth) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
            if (pen.CanFreeze) pen.Freeze();
            drawingContext.DrawGeometry(null, pen, Data);
        }
        drawingContext.Pop();
    }
}
