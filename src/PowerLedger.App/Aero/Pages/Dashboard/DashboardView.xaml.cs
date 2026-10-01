using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using PowerLedger.Contracts;

namespace PowerLedger.App.Aero;

/// <summary>A part's row beside the ring (the mockup's): its colour's glow, its name as the mockup words it, its model
/// and its share.</summary>
internal sealed record PartRow(Part Part, string Name, string? Model, string Share, Effect Glow);

/// <summary>
/// Aero's Dashboard (0.10.9, the liquid glass mockup's Main board) over <see cref="DashboardViewModel"/>. It asks the
/// ViewModel for Aero's figures (<see cref="DashboardViewModel.Detailed"/>) and reads the shell's household for the split
/// by PC. It carries on the window's intro (its panes rise after the sidebar's, their content glides in, then the charts
/// draw), and the tour (Settings' Play tour) pushes the camera in on each pane in turn; Esc or a click outside it comes
/// back. Under reduced motion nothing travels: the panes are there, and a focused pane only dims the others.
/// </summary>
public partial class DashboardView : UserControl
{
    /// <summary>Under this width the page is one column: the panes one under another.</summary>
    internal const double OneColumnBelow = 860;

    /// <summary>The mockup's rows at 1440 by 900: the top 280 high, the bottom the rest of its 790, 494; never less than
    /// <see cref="LeastBottom"/>, where the page scrolls instead.</summary>
    internal const double TopRow = 280;
    internal const double LeastBottom = 420;

    /// <summary>How far the intro's count-up has gone, 0 to 1 (the demo's <c>countUp</c>): Power now's watts, today's kWh
    /// and This month's figure show this much of their value while it runs, and all of it at 1, where it rests.</summary>
    internal static readonly DependencyProperty CountProperty = DependencyProperty.Register(nameof(Count), typeof(double), typeof(DashboardView),
        new PropertyMetadata(1.0, (d, _) => ((DashboardView)d).ShowCounted()));

    private DashboardViewModel? _model;
    private ShellViewModel? _shell;
    private bool _narrow;
    private GlassPanel? _focused;
    private double _monthFill;
    private string? _partsShown;
    private readonly List<DispatcherTimer> _tour = [];

    public DashboardView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => Attach(DataContext as DashboardViewModel);
        Loaded += OnLoaded;
        Unloaded += (_, _) => Detach();
        SizeChanged += (_, _) => Reflow();
        Scroller.SizeChanged += (_, _) => Reflow();
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape || _focused is null && !Touring) return;
            e.Handled = true;
            StopTour();
            Unfocus();
        };
        PreviewMouseLeftButtonDown += (_, e) =>
        {
            if (_focused is null || IsWithin(e.OriginalSource as DependencyObject, _focused)) return;
            StopTour();
            Unfocus();
        };
        MonthBar.SizeChanged += (_, _) => MonthFill.Width = MonthBar.ActualWidth * _monthFill;
    }

    /// <summary>The panes, in the order they rise.</summary>
    internal GlassPanel[] Panes => [PNow, PMonth, PDaily, PParts];

    /// <summary>The pane the camera is on, for a test; null at rest.</summary>
    internal GlassPanel? Focused => _focused;

    /// <summary>Whether the page is in one column, for a test.</summary>
    internal bool OneColumn => _narrow;

    /// <summary>The intro's count-up, 0 to 1; 1 at rest.</summary>
    internal double Count { get => (double)GetValue(CountProperty); set => SetValue(CountProperty, value); }

    /// <summary>Whether the tour is under way.</summary>
    internal bool Touring => _tour.Count > 0;

    private void Attach(DashboardViewModel? model)
    {
        if (_model is not null) _model.PropertyChanged -= OnModelChanged;
        _model = model;
        if (model is null) return;
        model.PropertyChanged += OnModelChanged;
        if (IsLoaded) model.Detailed = true;
        ShowAll();
    }

    private void Detach()
    {
        StopTour();
        if (_model is not null) _model.PropertyChanged -= OnModelChanged;
        if (_shell is not null)
        {
            _shell.Household.PropertyChanged -= OnHouseholdChanged;
            _shell = null;
        }
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_model is { } model)
        {
            model.PropertyChanged -= OnModelChanged;
            model.PropertyChanged += OnModelChanged;
            model.Detailed = true;
        }
        if (Window.GetWindow(this)?.DataContext is ShellViewModel shell && _shell is null)
        {
            _shell = shell;
            shell.Household.PropertyChanged += OnHouseholdChanged;
        }
        Reflow();
        ShowAll();
        Dispatcher.BeginInvoke(() => MoveSeg(animate: false), DispatcherPriority.Loaded);
        // The window's reveal: held with its panes until it has painted, or carried on at once when it is already under way.
        if (Window.GetWindow(this) is AeroWindow { IntroPending: true } window)
        {
            if (window.RevealHeld) HoldIntro();
            else PlayIntro(window.IntroPanes);
        }
        else DrawCharts();
    }

    // ---------------------------------------------------------------- figures

    private void OnModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(DashboardViewModel.Live):
                ShowNow();
                break;
            case nameof(DashboardViewModel.Parts):
                ShowParts();
                break;
            case nameof(DashboardViewModel.Month):
                ShowMonth();
                break;
            case nameof(DashboardViewModel.Detail):
                ShowNow();
                ShowMonth();
                Dispatcher.BeginInvoke(() => MoveSeg(animate: true), DispatcherPriority.Loaded);
                break;
        }
    }

    private void OnHouseholdChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(HouseholdViewModel.Members)) ShowMonth();
    }

    private void ShowAll()
    {
        ShowParts();
        ShowNow();
        ShowMonth();
    }

    /// <summary>Power now, what measures it, and today's kWh with the change against yesterday.</summary>
    private void ShowNow()
    {
        if (_model is not { } model) return;
        var culture = CultureInfo.CurrentCulture;
        var detail = model.Detail;
        var (value, unit) = DashboardFigures.PowerNow(model.Live.Watts, false, null, null, culture);
        var rolls = double.IsFinite(model.Live.Watts);
        if (rolls) NowRoll.Value = (int)Math.Round(Math.Max(0, model.Live.Watts) * Count);
        NowRoll.Visibility = rolls ? Visibility.Visible : Visibility.Collapsed;
        NowValue.Visibility = rolls ? Visibility.Collapsed : Visibility.Visible;
        NowValue.Text = value;
        NowUnit.Text = unit;
        AutomationProperties.SetName(NowFigure, unit.Length == 0 ? value : $"{value} {unit}");
        NowSource.Text = DashboardFigures.MeasuredBy(model.Live);
        NowSource.ToolTip = model.Live.QualityNote is { Length: > 0 } note ? note : null;
        TodayKwh.Text = detail is null ? Format.Missing : DashboardFigures.Counted(Format.Kwh(detail.TodayKwh, culture), Count, culture);
        ChangeText.Text = DashboardFigures.Change(detail?.ChangeVsYesterday, culture);
    }

    /// <summary>The parts beside the ring, given again only when what the list shows (the parts, their models and their
    /// shares as whole percents) has changed: the parts come new with every reading, and a list given them each second
    /// built its rows again each second, a layout pass and a redraw of the whole pane (Plan U).</summary>
    private void ShowParts()
    {
        var parts = _model?.Parts ?? [];
        var culture = CultureInfo.CurrentCulture;
        var shown = string.Join("\n", parts.Select(p => $"{p.Part}|{p.Name}|{p.Model}|{p.Share.ToString("0%", culture)}"));
        if (shown == _partsShown && PartsList.ItemsSource is not null) return;
        _partsShown = shown;
        PartsList.ItemsSource = parts.Select(p => new PartRow(p.Part, Named(p), p.Model, p.Share.ToString("0%", culture), Glow(p.Part))).ToList();
    }

    /// <summary>The mockup's words for a part: Graphics for the GPU.</summary>
    private static string Named(DashboardPart part) => part.Part == Part.Gpu ? "Graphics" : part.Name;

    /// <summary>A part's dot glows in its own colour (the mockup's 0 0 8px).</summary>
    private Effect Glow(Part part)
    {
        var key = part switch { Part.Cpu => "M.PartCpu", Part.Gpu => "M.PartGpu", Part.Display => "M.PartDisplay", _ => "M.PartRest" };
        var colour = TryFindResource(key) is SolidColorBrush brush ? brush.Color : Colors.White;
        var glow = new DropShadowEffect { ShadowDepth = 0, BlurRadius = 8, Opacity = 1, Color = colour, RenderingBias = RenderingBias.Performance };
        glow.Freeze();
        return glow;
    }

    /// <summary>This month: the cost so far, the energy and where it is heading, the month's bar, and each PC's share.</summary>
    private void ShowMonth()
    {
        if (_model is not { } model) return;
        ShowMonthBig();
        MonthSub.Text = DashboardFigures.MonthSub(model.Month, energy: false);
        if (model.Detail is { } detail)
        {
            _monthFill = DashboardMaths.Fill(detail.MonthDay, detail.MonthDays);
            MonthFill.Width = MonthBar.ActualWidth * _monthFill;
            AutomationProperties.SetName(MonthBar, DashboardFigures.DayOf(detail.MonthDay, detail.MonthDays));
        }
        var rows = YourPcs.Rows(_shell?.Household.Members ?? [], model.Live.Watts, CultureInfo.CurrentCulture);
        MonthSplit.ItemsSource = rows.Take(3).ToList();
    }

    /// <summary>This month's big figure, as far as the count-up has gone.</summary>
    private void ShowMonthBig()
    {
        if (_model is { } model)
            MonthBig.Text = DashboardFigures.Counted(DashboardFigures.MonthBig(model.Month, false), Count, CultureInfo.CurrentCulture);
    }

    /// <summary>A frame of the count-up: the counted figures again, nothing else.</summary>
    private void ShowCounted()
    {
        ShowNow();
        ShowMonthBig();
    }

    // ---------------------------------------------------------------- the controls

    private void OpenReportClick(object sender, RoutedEventArgs e)
    {
        if (_shell is not null) _shell.Page = Page.Report;
    }

    /// <summary>Day, Week or Month: the bars read again for the span, and the bubble slides to the word.</summary>
    private void SpanChecked(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton { Content: string name } || _model is not { } model) return;
        var span = Enum.Parse<HistorySpan>(name);
        MoveSeg(animate: true);
        if (model.HistorySpan == span) return;
        model.HistorySpan = span;
    }

    /// <summary>The glass bubble slides and stretches to the chosen span on the spring.</summary>
    private void MoveSeg(bool animate)
    {
        if (!IsLoaded) return;
        var chosen = SegItems.Children.OfType<RadioButton>().FirstOrDefault(r => r.IsChecked == true);
        if (chosen is null || chosen.ActualWidth == 0) return;
        var x = chosen.TranslatePoint(new Point(0, 0), Seg).X;
        var move = animate && SegIndicator.Opacity > 0 ? AeroMotion.SegPill : 0;
        AeroMotion.Move(SegX, TranslateTransform.XProperty, x, move, AeroMotion.Spring);
        AeroMotion.Move(SegIndicator, WidthProperty, chosen.ActualWidth, move, AeroMotion.Spring);
        SegIndicator.Opacity = 1;
    }

    private void PartEnter(object sender, MouseEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: PartRow part } row)
        {
            Ring.Hovered = part.Part;
            row.SetResourceReference(Border.BackgroundProperty, "A.B.Well");
        }
    }

    private void PartLeave(object sender, MouseEventArgs e)
    {
        Ring.Hovered = null;
        if (sender is Border row) row.Background = Brushes.Transparent;
    }

    // ---------------------------------------------------------------- one column or two

    /// <summary>Two columns as the mockup lays them (Power now and This month 280 high, Energy each day and Where the power
    /// goes the rest of the page, 494 at 1440 by 900, the right column 384 wide), or the panes one under another under
    /// <see cref="OneColumnBelow"/>.</summary>
    private void Reflow()
    {
        var narrow = ActualWidth > 0 && ActualWidth < OneColumnBelow;
        // The scroller's own height, not its ViewportHeight, which it brings up to date only a layout pass later: a window
        // grown by 18 kept the bottom row at its old height.
        var bottom = Math.Max(LeastBottom, Scroller.ActualHeight > 0 ? Scroller.ActualHeight - TopRow - 16 : 494);
        _narrow = narrow;
        Layout.RowDefinitions.Clear();
        Layout.ColumnDefinitions[1].Width = new GridLength(narrow ? 0 : 16);
        Layout.ColumnDefinitions[2].Width = new GridLength(narrow ? 0 : 384);
        (GlassPanel Pane, double Height)[] order = narrow
            ? [(PNow, TopRow), (PMonth, TopRow), (PDaily, LeastBottom), (PParts, 494)]
            : [(PNow, TopRow), (PDaily, bottom)];
        for (var i = 0; i < order.Length; i++)
        {
            if (i > 0) Layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(16) });
            Layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(order[i].Height) });
            Grid.SetRow(order[i].Pane, i * 2);
            Grid.SetColumn(order[i].Pane, 0);
            Grid.SetColumnSpan(order[i].Pane, narrow ? 3 : 1);
        }
        if (!narrow)
        {
            Grid.SetRow(PMonth, 0);
            Grid.SetColumn(PMonth, 2);
            Grid.SetColumnSpan(PMonth, 1);
            Grid.SetRow(PParts, 2);
            Grid.SetColumn(PParts, 2);
            Grid.SetColumnSpan(PParts, 1);
        }
    }

    // ---------------------------------------------------------------- the intro and the charts

    /// <summary>What glides in inside the panes.</summary>
    private FrameworkElement[] Contents => [NowContent, MonthContent, DailyContent, PartsContent];

    /// <summary>The panes held at the reveal's start (clear, down, smaller), the charts undrawn and the figures at
    /// nothing, until the window has painted and plays it (0.10.6).</summary>
    internal void HoldIntro()
    {
        foreach (var pane in Panes) AeroWindow.Held(pane, AeroMotion.PaneRise, AeroMotion.PaneScale);
        foreach (var content in Contents) AeroWindow.Held(content, AeroMotion.ContentRise, 1);
        Undraw();
    }

    /// <summary>The charts undrawn and the figures at nothing, with no clock holding them where they were.</summary>
    private void Undraw()
    {
        Live.BeginAnimation(LiveChart.RevealProperty, null);
        Ring.BeginAnimation(DonutChart.SweepProperty, null);
        Live.Reveal = 0;
        Ring.Sweep = 0;
        Bars.Hold();
        if (AeroMotion.Reduced) return;
        BeginAnimation(CountProperty, null);
        Count = 0;   // the figures wait at nothing while the panes rise
    }

    /// <summary>The window's intro carried on (design §1): the panes rise after the window's own, their content glides in,
    /// then the charts draw.</summary>
    internal void PlayIntro(int after)
    {
        var panes = Panes;
        for (var i = 0; i < panes.Length; i++) AeroWindow.Rise(panes[i], after + i);
        var contents = Contents;
        for (var i = 0; i < contents.Length; i++) AeroWindow.Glide(contents[i], after + i);
        Undraw();
        var start = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(Math.Max(1, AeroMotion.MoveMs(AeroMotion.ChartsStart))) };
        start.Tick += (_, _) =>
        {
            start.Stop();
            DrawCharts();
            CountUp();
        };
        start.Start();
    }

    /// <summary>The intro's figures count up from nothing with the charts (the demo's countUp, 1.1 s on its curve), then
    /// the count lets go: no clock is left running or holding (Plan U). Under reduced motion they are simply there.</summary>
    private void CountUp()
        => AeroMotion.Move(this, CountProperty, 1, AeroMotion.CountUp, AeroMotion.Quart, from: 0, done: () =>
        {
            SetValue(CountProperty, 1.0);
            BeginAnimation(CountProperty, null);
        });

    /// <summary>The charts draw in: Last minute's line from the left, the bars growing from their feet in turn, the ring
    /// sweeping round, the month's bar growing. Under reduced motion they are simply there.</summary>
    private void DrawCharts()
    {
        AeroMotion.Move(Live, LiveChart.RevealProperty, 1, AeroMotion.LineDraw, AeroMotion.Glide, from: 0);
        Bars.Grow(AeroMotion.BarGrow, AeroMotion.Stagger / 2);
        AeroMotion.Move(Ring, DonutChart.SweepProperty, 1, AeroMotion.PieRise, AeroMotion.Glide, from: 0,
            done: () =>
            {
                Ring.SetValue(DonutChart.SweepProperty, 1.0);
                Ring.BeginAnimation(DonutChart.SweepProperty, null);
            });
        // The bar grows on a scale from its left end, not its width: a width asks for a layout pass every frame (Plan U).
        MonthFill.Width = MonthBar.ActualWidth * _monthFill;
        var grow = new ScaleTransform(0, 1);
        MonthFill.RenderTransformOrigin = new Point(0, .5);
        MonthFill.RenderTransform = grow;
        AeroMotion.Move(grow, ScaleTransform.ScaleXProperty, 1, AeroMotion.BarGrow, AeroMotion.Glide, from: 0,
            done: () => MonthFill.RenderTransform = Transform.Identity);
    }

    // ---------------------------------------------------------------- the camera

    /// <summary>
    /// The camera push-in (design §1): the page scales and slides so <paramref name="pane"/> fills about three quarters
    /// of the view, on the glide, and the other panes dim; under reduced motion the others only dim.
    /// </summary>
    internal void FocusPane(GlassPanel pane)
    {
        var at = pane.TranslatePoint(new Point(0, 0), Layout);
        double w = pane.ActualWidth, h = pane.ActualHeight, vw = Scroller.ViewportWidth, vh = Scroller.ViewportHeight;
        if (w <= 0 || h <= 0 || vw <= 0 || vh <= 0) return;
        var s = Math.Clamp(Math.Min(Math.Min(vw * .74 / w, vh * .74 / h), 2.1), 1, 2.1);
        var tx = vw / 2 - (at.X + w / 2) * s;
        var ty = Scroller.VerticalOffset + vh / 2 - (at.Y + h / 2) * s;
        AeroMotion.Move(CamScale, ScaleTransform.ScaleXProperty, s, AeroMotion.Camera, AeroMotion.Glide);
        AeroMotion.Move(CamScale, ScaleTransform.ScaleYProperty, s, AeroMotion.Camera, AeroMotion.Glide);
        AeroMotion.Move(CamShift, TranslateTransform.XProperty, tx, AeroMotion.Camera, AeroMotion.Glide);
        AeroMotion.Move(CamShift, TranslateTransform.YProperty, ty, AeroMotion.Camera, AeroMotion.Glide);
        (Window.GetWindow(this) as AeroWindow)?.AlignFrostFor(AeroMotion.MoveMs(AeroMotion.Camera));
        var dim = TryFindResource("A.Glass.DimmedOpacity") is double d ? d : .22;
        foreach (var other in Panes)
        {
            AeroMotion.Fade(other, OpacityProperty, other == pane ? 1 : dim, AeroMotion.FocusFade, AeroMotion.Glide);
            other.IsHitTestVisible = other == pane;
        }
        pane.IsHitTestVisible = true;
        _focused = pane;
    }

    /// <summary>Back to the whole page.</summary>
    internal void Unfocus()
    {
        if (_focused is null) return;
        AeroMotion.Move(CamScale, ScaleTransform.ScaleXProperty, 1, AeroMotion.Camera, AeroMotion.Glide);
        AeroMotion.Move(CamScale, ScaleTransform.ScaleYProperty, 1, AeroMotion.Camera, AeroMotion.Glide);
        AeroMotion.Move(CamShift, TranslateTransform.XProperty, 0, AeroMotion.Camera, AeroMotion.Glide);
        AeroMotion.Move(CamShift, TranslateTransform.YProperty, 0, AeroMotion.Camera, AeroMotion.Glide);
        (Window.GetWindow(this) as AeroWindow)?.AlignFrostFor(AeroMotion.MoveMs(AeroMotion.Camera));
        foreach (var pane in Panes)
        {
            AeroMotion.Fade(pane, OpacityProperty, 1, AeroMotion.FocusFade, AeroMotion.Glide);
            pane.IsHitTestVisible = true;
        }
        _focused = null;
    }

    /// <summary>
    /// The tour (Settings' Play tour): the camera pushes in on Power now, This month, Energy each day and Where the power
    /// goes in turn, <see cref="AeroMotion.TourStep"/> apart, then comes back to the whole page. A click outside the pane
    /// in focus, Esc, the page changing or the intro replaying ends it.
    /// </summary>
    internal void PlayTour()
    {
        StopTour();
        GlassPanel[] stops = [PNow, PMonth, PDaily, PParts];
        for (var i = 0; i < stops.Length; i++)
        {
            var pane = stops[i];
            At(i * AeroMotion.TourStep, () => FocusPane(pane));
        }
        At(stops.Length * AeroMotion.TourStep, () =>
        {
            StopTour();
            Unfocus();
        });
    }

    /// <summary>Ends the tour where it is; the camera stays on the pane it had reached.</summary>
    internal void StopTour()
    {
        foreach (var timer in _tour) timer.Stop();
        _tour.Clear();
    }

    private void At(double ms, Action step)
    {
        var timer = new DispatcherTimer(DispatcherPriority.Normal, Dispatcher) { Interval = TimeSpan.FromMilliseconds(Math.Max(1, ms)) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            _tour.Remove(timer);
            if (IsLoaded) step();
        };
        _tour.Add(timer);
        timer.Start();
    }

    private static bool IsWithin(DependencyObject? node, DependencyObject scope)
    {
        while (node is not null)
        {
            if (node == scope) return true;
            node = node is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node);
        }
        return false;
    }
}
