using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using PowerLedger.Contracts;

namespace PowerLedger.App.Aero;

/// <summary>
/// Aero's Dashboard (Aero look design §1, Plan S D3) over <see cref="DashboardViewModel"/>. It asks the ViewModel for
/// Aero's figures (<see cref="DashboardViewModel.Detailed"/>) and reads the shell's Insights for the forecast and its
/// household for the split by PC. It carries on the window's intro (its panes rise after the sidebar's, their content
/// glides in, then the charts draw), and pushes the camera in on a pane whose expand button is pressed; Esc or a click
/// outside it comes back. Under reduced motion nothing travels: the panes are there, and a focused pane only dims the
/// others.
/// </summary>
public partial class DashboardView : UserControl
{
    /// <summary>Under this width the page is one column: the panes one under another.</summary>
    internal const double OneColumnBelow = 860;

    /// <summary>How far the intro's count-up has gone, 0 to 1 (the demo's <c>countUp</c>): Power now's watts, today's kWh
    /// and This month's figure show this much of their value while it runs, and all of it at 1, where it rests.</summary>
    internal static readonly DependencyProperty CountProperty = DependencyProperty.Register(nameof(Count), typeof(double), typeof(DashboardView),
        new PropertyMetadata(1.0, (d, _) => ((DashboardView)d).ShowCounted()));

    private DashboardViewModel? _model;
    private ShellViewModel? _shell;
    private bool _costMode;
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
    internal GlassPanel[] Panes => [PNow, PMonth, PDaily, PParts, PHist];

    /// <summary>The pane the camera is on, for a test; null at rest.</summary>
    internal GlassPanel? Focused => _focused;

    /// <summary>Whether the page is in one column, for a test.</summary>
    internal bool OneColumn => _narrow;

    /// <summary>The intro's count-up, 0 to 1; 1 at rest.</summary>
    internal double Count { get => (double)GetValue(CountProperty); set => SetValue(CountProperty, value); }

    /// <summary>Whether the tour (the demo's Play tour) is under way.</summary>
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
            if (_shell.Insights is { } insights) insights.PropertyChanged -= OnInsightsChanged;
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
            if (shell.Insights is { } insights) insights.PropertyChanged += OnInsightsChanged;
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
                ShowDaily();
                ShowHistory();
                break;
            case nameof(DashboardViewModel.HistoryShown):
                ShowHistory();
                break;
            case nameof(DashboardViewModel.Saved):
                if (_model?.Saved is { } saved) (Window.GetWindow(this) as AeroWindow)?.Toast(saved);
                break;
        }
    }

    private void OnHouseholdChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(HouseholdViewModel.Members)) ShowMonth();
    }

    private void OnInsightsChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(InsightsViewModel.Report)) ShowMonth();
    }

    private void ShowAll()
    {
        ShowParts();
        ShowNow();
        ShowMonth();
        ShowDaily();
        ShowHistory();
    }

    /// <summary>Power now and today's kWh with the change against yesterday.</summary>
    private void ShowNow()
    {
        if (_model is not { } model) return;
        var culture = CultureInfo.CurrentCulture;
        var detail = model.Detail;
        var (value, unit) = DashboardFigures.PowerNow(model.Live.Watts, _costMode, detail?.PricePerKwh, detail?.Currency, culture);
        var rolls = !_costMode || detail?.PricePerKwh is null;
        rolls &= double.IsFinite(model.Live.Watts);
        if (rolls) NowRoll.Value = (int)Math.Round(Math.Max(0, model.Live.Watts) * Count);
        NowRoll.Visibility = rolls ? Visibility.Visible : Visibility.Collapsed;
        NowValue.Visibility = rolls ? Visibility.Collapsed : Visibility.Visible;
        NowValue.Text = DashboardFigures.Counted(value, Count, culture);
        NowUnit.Text = unit;
        AutomationProperties.SetName(NowFigure, unit.Length == 0 ? value : $"{value} {unit}");
        UnitButton.IsEnabled = detail?.PricePerKwh is not null;
        TodayKwh.Text = detail is null ? Format.Missing : DashboardFigures.Counted(Format.Kwh(detail.TodayKwh, culture), Count, culture);
        ChangeText.Text = DashboardFigures.Change(detail?.ChangeVsYesterday, culture);
    }

    /// <summary>The parts beside the pie, given again only when what the list shows (the parts, their models and their
    /// shares as whole percents) has changed: the parts come new with every reading, and a list given them each second
    /// built its rows again each second, a layout pass and a redraw of the whole pane (Plan U).</summary>
    private void ShowParts()
    {
        var parts = _model?.Parts ?? [];
        var shown = string.Join("\n", parts.Select(p => $"{p.Part}|{p.Name}|{p.Model}|{p.Share.ToString("0%", CultureInfo.CurrentCulture)}"));
        if (shown == _partsShown && PartsList.ItemsSource is not null) return;
        _partsShown = shown;
        PartsList.ItemsSource = parts;
    }

    /// <summary>This month: cost or energy, the day of the month and its bar, the forecast, and the split by PC.</summary>
    private void ShowMonth()
    {
        if (_model is not { } model) return;
        var energy = MonthEnergy.IsChecked == true;
        ShowMonthBig();
        MonthSub.Text = DashboardFigures.MonthSub(model.Month, energy);
        if (model.Detail is { } detail)
        {
            DayOfText.Text = DashboardFigures.DayOf(detail.MonthDay, detail.MonthDays);
            _monthFill = DashboardMaths.Fill(detail.MonthDay, detail.MonthDays);
            MonthFill.Width = MonthBar.ActualWidth * _monthFill;
        }
        var forecast = DashboardFigures.Forecast(_shell?.Insights?.Report?.Forecast, CultureInfo.CurrentCulture);
        ForecastText.Text = forecast ?? "";
        ForecastText.Visibility = forecast is null ? Visibility.Collapsed : Visibility.Visible;
        var rows = YourPcs.Rows(_shell?.Household.Members ?? [], model.Live.Watts, CultureInfo.CurrentCulture);
        MonthSplit.ItemsSource = rows.Take(3).ToList();
    }

    /// <summary>This month's big figure, as far as the count-up has gone.</summary>
    private void ShowMonthBig()
    {
        if (_model is { } model)
            MonthBig.Text = DashboardFigures.Counted(DashboardFigures.MonthBig(model.Month, MonthEnergy.IsChecked == true), Count, CultureInfo.CurrentCulture);
    }

    /// <summary>A frame of the count-up: the counted figures again, nothing else.</summary>
    private void ShowCounted()
    {
        ShowNow();
        ShowMonthBig();
    }

    /// <summary>Energy each day's month button and legend.</summary>
    private void ShowDaily()
    {
        if (_model?.Detail is not { } detail) return;
        var culture = CultureInfo.CurrentCulture;
        var month = detail.DailyMonth;
        var before = month.AddMonths(-1);
        MonthLabel.Text = month.ToString("MMMM", culture);
        LegendName.Text = month.ToString("MMMM", culture);
        LegendKwh.Text = Format.Kwh(detail.DailyKwh, culture) + " kWh";
        LegendPrevious.Text = before.ToString("MMMM", culture);
        LegendPreviousKwh.Text = Format.Kwh(detail.DailyPreviousKwh, culture) + " kWh";
        AutomationProperties.SetName(MonthButton, "Month, " + month.ToString("MMMM yyyy", culture));
    }

    /// <summary>The table's head names the span; an empty search says so.</summary>
    private void ShowHistory()
    {
        if (_model is not { } model) return;
        SpanHead.Text = (model.Detail?.Span ?? model.HistorySpan).ToString();
        var empty = model.HistoryShown.Count == 0 && model.Detail is not null;
        HistEmpty.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        HistEmpty.Text = string.IsNullOrWhiteSpace(model.HistoryQuery)
            ? "No history yet."
            : $"Nothing in {SpanHead.Text.ToLowerInvariant()} history matches \"{model.HistoryQuery.Trim()}\". Try a day name or a month.";
        Dispatcher.BeginInvoke(() => MoveSeg(animate: true), DispatcherPriority.Loaded);
    }

    // ---------------------------------------------------------------- the controls

    /// <summary>The unit button: watts, or what an hour costs at this rate.</summary>
    private void UnitClick(object sender, RoutedEventArgs e)
    {
        _costMode = !_costMode;
        var name = _costMode ? "Show watts" : "Show cost per hour";
        AutomationProperties.SetName(UnitButton, name);
        UnitButton.ToolTip = name;
        ShowNow();
    }

    private void MonthUnitChecked(object sender, RoutedEventArgs e) => ShowMonth();

    private void OpenReportClick(object sender, RoutedEventArgs e)
    {
        if (_shell is not null) _shell.Page = Page.Report;
    }

    /// <summary>The month picker: every month the history has, newest first, as a glass menu under the button.</summary>
    private void MonthPickerClick(object sender, RoutedEventArgs e)
    {
        if (_model is not { } model) return;
        var menu = new ContextMenu { PlacementTarget = MonthButton, Placement = PlacementMode.Bottom };
        menu.SetResourceReference(StyleProperty, "A.Menu");
        menu.SetResourceReference(ItemsControl.ItemContainerStyleProperty, "A.MenuItem");
        foreach (var month in model.DailyMonths)
        {
            var item = new MenuItem
            {
                Header = month.ToString(month.Year == DateTime.Now.Year ? "MMMM" : "MMMM yyyy", CultureInfo.CurrentCulture),
                IsCheckable = true, IsChecked = month == model.DailyMonth,
            };
            item.Click += (_, _) =>
            {
                model.DailyMonth = month;
                AeroMotion.Move(Daily, DailyChart.RevealProperty, 1, AeroMotion.DailyWipe, AeroMotion.Glide, from: 0);
            };
            menu.Items.Add(item);
        }
        menu.Closed += (_, _) => MonthButton.ContextMenu = null;
        MonthButton.ContextMenu = menu;
        menu.IsOpen = true;
    }

    private void SpanChecked(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton { Content: string name } || _model is not { } model) return;
        var span = Enum.Parse<HistorySpan>(name);
        if (model.HistorySpan == span) return;
        AeroMotion.Fade(HistRows, OpacityProperty, 0, AeroMotion.TableFade, null, done: () =>
        {
            model.HistorySpan = span;
            AeroMotion.Fade(HistRows, OpacityProperty, 1, AeroMotion.TableFade);
        });
        MoveSeg(animate: true);
    }

    /// <summary>The white pill slides and stretches to the chosen span on the spring.</summary>
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
        if (sender is FrameworkElement { DataContext: DashboardPart part } row)
        {
            Pie.Hovered = part.Part;
            row.SetResourceReference(Border.BackgroundProperty, "A.B.Well");
        }
    }

    private void PartLeave(object sender, MouseEventArgs e)
    {
        Pie.Hovered = null;
        if (sender is Border row) row.Background = Brushes.Transparent;
    }

    // ---------------------------------------------------------------- one column or two

    /// <summary>Two columns as the demo, or the panes one under another under <see cref="OneColumnBelow"/>.</summary>
    private void Reflow()
    {
        var narrow = ActualWidth > 0 && ActualWidth < OneColumnBelow;
        if (narrow == _narrow && Layout.RowDefinitions.Count > 0 && IsLoaded) return;
        _narrow = narrow;
        Layout.RowDefinitions.Clear();
        Layout.ColumnDefinitions[1].Width = new GridLength(narrow ? 0 : 16);
        Layout.ColumnDefinitions[2].Width = new GridLength(narrow ? 0 : 380);
        (GlassPanel Pane, double Height)[] order = narrow
            ? [(PNow, 262), (PMonth, double.NaN), (PDaily, 238), (PParts, 238), (PHist, double.NaN)]
            : [(PNow, 262), (PDaily, 238), (PHist, double.NaN)];
        for (var i = 0; i < order.Length; i++)
        {
            if (i > 0) Layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(16) });
            Layout.RowDefinitions.Add(new RowDefinition { Height = double.IsNaN(order[i].Height) ? GridLength.Auto : new GridLength(order[i].Height) });
            Grid.SetRow(order[i].Pane, i * 2);
            Grid.SetColumn(order[i].Pane, 0);
            Grid.SetColumnSpan(order[i].Pane, narrow || order[i].Pane == PHist ? 3 : 1);
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
    private FrameworkElement[] Contents => [NowContent, MonthContent, DailyContent, PartsContent, HistContent];

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
        Daily.BeginAnimation(DailyChart.RevealProperty, null);
        Pie.BeginAnimation(PieChart3D.RiseProperty, null);
        Live.Reveal = 0;
        Daily.Reveal = 0;
        Pie.Rise = 0;
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

    /// <summary>The charts draw in: Last minute's line from the left, the month wiped in, the pie's slices rising in turn,
    /// the month's bar growing. Under reduced motion they are simply there.</summary>
    private void DrawCharts()
    {
        AeroMotion.Move(Live, LiveChart.RevealProperty, 1, AeroMotion.LineDraw, AeroMotion.Glide, from: 0);
        AeroMotion.Move(Daily, DailyChart.RevealProperty, 1, AeroMotion.DailyWipe, AeroMotion.Glide, from: 0);
        Pie.PlayRise();
        // The bar grows on a scale from its left end, not its width: a width asks for a layout pass every frame (Plan U).
        MonthFill.Width = MonthBar.ActualWidth * _monthFill;
        var grow = new ScaleTransform(0, 1);
        MonthFill.RenderTransformOrigin = new Point(0, .5);
        MonthFill.RenderTransform = grow;
        AeroMotion.Move(grow, ScaleTransform.ScaleXProperty, 1, AeroMotion.BarGrow, AeroMotion.Glide, from: 0,
            done: () => MonthFill.RenderTransform = Transform.Identity);
        // So do the split's bars, as the demo's (every [data-w] bar grows with the count).
        foreach (var fill in SplitFills())
        {
            var bar = new ScaleTransform(0, 1);
            fill.RenderTransformOrigin = new Point(0, .5);
            fill.RenderTransform = bar;
            AeroMotion.Move(bar, ScaleTransform.ScaleXProperty, 1, AeroMotion.BarGrow, AeroMotion.Glide, from: 0,
                done: () => fill.RenderTransform = Transform.Identity);
        }
    }

    /// <summary>The split's bar fills, each PC's.</summary>
    private List<Border> SplitFills()
    {
        var fills = new List<Border>();
        void Walk(DependencyObject node)
        {
            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
            {
                var child = VisualTreeHelper.GetChild(node, i);
                if (child is Border { Name: "SplitFill" } fill) fills.Add(fill);
                else Walk(child);
            }
        }
        Walk(MonthSplit);
        return fills;
    }

    // ---------------------------------------------------------------- the camera

    private void FocusClick(object sender, RoutedEventArgs e)
    {
        StopTour();
        if (sender is not FrameworkElement { Tag: string name } || FindName(name) is not GlassPanel pane) return;
        if (_focused == pane) Unfocus();
        else FocusPane(pane);
    }

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
    /// The demo's Play tour: the camera pushes in on Power now, Energy each day, Where the power goes and History in turn,
    /// <see cref="AeroMotion.TourStep"/> apart, then comes back to the whole page. A click outside the pane in focus, an
    /// expand button, Esc, the page changing or the intro replaying ends it.
    /// </summary>
    internal void PlayTour()
    {
        StopTour();
        GlassPanel[] stops = [PNow, PDaily, PParts, PHist];
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

/// <summary>A share as a star column's width, or with the parameter "Rest" the rest of the whole: a bar's fill and its
/// track side by side, so the fill needs no measuring.</summary>
internal sealed class ShareColumn : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var share = value is double d && double.IsFinite(d) ? Math.Clamp(d, 0, 1) : 0;
        return new GridLength(parameter as string == "Rest" ? 1 - share : share, GridUnitType.Star);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}
