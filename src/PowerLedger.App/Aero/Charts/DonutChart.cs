using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Media;
using System.Windows.Media.Effects;
using PowerLedger.Contracts;
using MediaGeometry = System.Windows.Media.Geometry;

namespace PowerLedger.App.Aero;

/// <summary>One part's arc of the ring: its part, and where it starts and ends, as fractions of the turn clockwise from
/// the top.</summary>
internal sealed record RingArc(Part Part, double From, double To);

/// <summary>The ring's arcs (the mockup's conic gradient), pure: each part's share of the whole, clockwise from the top in
/// the parts' order, an empty share left out.</summary>
internal static class RingArcs
{
    public static IReadOnlyList<RingArc> From(IReadOnlyList<(Part Part, double Share)> parts)
    {
        var total = parts.Where(p => double.IsFinite(p.Share) && p.Share > 0).Sum(p => p.Share);
        if (total <= 0) return [];
        var arcs = new List<RingArc>();
        var at = 0.0;
        foreach (var (part, share) in parts)
        {
            if (!double.IsFinite(share) || share <= 0) continue;
            var end = at + share / total;
            arcs.Add(new RingArc(part, at, end));
            at = end;
        }
        return arcs;
    }
}

/// <summary>
/// Where the power goes (0.10.9, the liquid glass mockup's donut): each part an arc of a ring in its colour, clockwise
/// from the top, with the mockup's soft shadow under it (0 18px 30px -14px, navy at 60 %); its hole is a glass disc the
/// view lays over it. The intro sweeps it round through <see cref="Sweep"/>; the part under the pointer in the list
/// stands a little proud. Drawn on demand only: nothing runs at rest.
/// </summary>
internal sealed class DonutChart : FrameworkElement
{
    public static readonly DependencyProperty PartsProperty = DependencyProperty.Register(nameof(Parts), typeof(IReadOnlyList<DashboardPart>),
        typeof(DonutChart), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, (d, _) => ((DonutChart)d).OnParts()));

    public static readonly DependencyProperty SweepProperty = DependencyProperty.Register(nameof(Sweep), typeof(double),
        typeof(DonutChart), new FrameworkPropertyMetadata(1.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty HoveredProperty = DependencyProperty.Register(nameof(Hovered), typeof(Part?),
        typeof(DonutChart), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    private static readonly DependencyProperty CpuProperty = Ink("Cpu");
    private static readonly DependencyProperty GpuProperty = Ink("Gpu");
    private static readonly DependencyProperty DisplayProperty = Ink("Display");
    private static readonly DependencyProperty RestProperty = Ink("Rest");

    /// <summary>The hole's radius as a share of the ring's: the mockup's 100 px disc in its 180.</summary>
    public const double Hole = 50.0 / 90.0;

    private IReadOnlyList<RingArc> _arcs = [];

    public DonutChart()
    {
        SetResourceReference(CpuProperty, "M.PartCpu");
        SetResourceReference(GpuProperty, "M.PartGpu");
        SetResourceReference(DisplayProperty, "M.PartDisplay");
        SetResourceReference(RestProperty, "M.PartRest");
        AutomationProperties.SetName(this, "Where the power goes");
        var shadow = new DropShadowEffect { Direction = 270, ShadowDepth = 12, BlurRadius = 26, Opacity = .5, Color = Color.FromRgb(0x00, 0x05, 0x1E), RenderingBias = RenderingBias.Performance };
        shadow.Freeze();
        Effect = shadow;
    }

    public IReadOnlyList<DashboardPart>? Parts { get => (IReadOnlyList<DashboardPart>?)GetValue(PartsProperty); set => SetValue(PartsProperty, value); }

    /// <summary>How far round the ring is drawn, 0 to 1.</summary>
    public double Sweep { get => (double)GetValue(SweepProperty); set => SetValue(SweepProperty, value); }

    /// <summary>The part under the pointer in the list, or null.</summary>
    public Part? Hovered { get => (Part?)GetValue(HoveredProperty); set => SetValue(HoveredProperty, value); }

    /// <summary>The arcs drawn, for a test.</summary>
    internal IReadOnlyList<RingArc> Arcs => _arcs;

    private static DependencyProperty Ink(string name) => DependencyProperty.Register(name + "Ink", typeof(Brush), typeof(DonutChart),
        new FrameworkPropertyMetadata(Brushes.Gray, FrameworkPropertyMetadataOptions.AffectsRender));

    private void OnParts()
    {
        var parts = Parts ?? [];
        _arcs = RingArcs.From([.. parts.Select(p => (p.Part, p.Share))]);
        AutomationProperties.SetHelpText(this, string.Join(", ", parts.Where(p => p.Share > 0).Select(p => $"{p.Name} {p.Share.ToString("0%", CultureInfo.CurrentCulture)}")));
    }

    private Brush InkOf(Part part) => (Brush)GetValue(part switch
    {
        Part.Cpu => CpuProperty,
        Part.Gpu => GpuProperty,
        Part.Display => DisplayProperty,
        _ => RestProperty,
    });

    protected override void OnRender(DrawingContext dc)
    {
        var size = Math.Min(ActualWidth, ActualHeight);
        if (size <= 0) return;
        var centre = new Point(ActualWidth / 2, ActualHeight / 2);
        var outer = size / 2;   // the part under the pointer stands proud past it
        var inner = outer * Hole;
        var sweep = Math.Clamp(Sweep, 0, 1);
        if (_arcs.Count == 0)
        {
            dc.DrawGeometry(null, new Pen(new SolidColorBrush(Color.FromArgb(0x30, 255, 255, 255)), outer - inner), Circle(centre, (outer + inner) / 2));
            return;
        }
        foreach (var arc in _arcs)
        {
            var from = Math.Min(arc.From, sweep);
            var to = Math.Min(arc.To, sweep);
            if (to - from <= 1e-6) continue;
            var proud = Hovered == arc.Part ? 4 : 0;
            dc.DrawGeometry(InkOf(arc.Part), null, Segment(centre, inner, outer + proud, from, to));
        }
    }

    private static EllipseGeometry Circle(Point centre, double r) => new(centre, r, r);

    /// <summary>The ring between <paramref name="inner"/> and <paramref name="outer"/> from <paramref name="from"/> to
    /// <paramref name="to"/> of the turn, clockwise from the top.</summary>
    internal static MediaGeometry Segment(Point centre, double inner, double outer, double from, double to)
    {
        if (to - from >= 1 - 1e-9)
        {
            var ring = new CombinedGeometry(GeometryCombineMode.Exclude, Circle(centre, outer), Circle(centre, inner));
            ring.Freeze();
            return ring;
        }
        Point At(double r, double t) => new(centre.X + r * Math.Sin(t * 2 * Math.PI), centre.Y - r * Math.Cos(t * 2 * Math.PI));
        var large = to - from > .5;
        var figure = new PathFigure { StartPoint = At(outer, from), IsClosed = true, IsFilled = true };
        figure.Segments.Add(new ArcSegment(At(outer, to), new Size(outer, outer), 0, large, SweepDirection.Clockwise, true));
        figure.Segments.Add(new LineSegment(At(inner, to), true));
        figure.Segments.Add(new ArcSegment(At(inner, from), new Size(inner, inner), 0, large, SweepDirection.Counterclockwise, true));
        var geometry = new PathGeometry([figure]);
        geometry.Freeze();
        return geometry;
    }
}
