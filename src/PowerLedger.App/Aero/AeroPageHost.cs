using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using PowerLedger.App.Aero;

namespace PowerLedger.App;

/// <summary>
/// Where an Aero page shows (Aero look design §2), with its transition: the page leaving fades out quickly while the one
/// arriving fades in and glides up <see cref="AeroMotion.ContentRise"/> on the glide curve; under reduced motion only the
/// fades are left. The view is chosen by <see cref="Page"/> as well as by the content's type, since the Dashboard and
/// Parts pages are two views over one <see cref="DashboardViewModel"/>. Views are found by name in this assembly, so a
/// page whose view another agent builds (Plan S Wave 1: P's History, Parts, Reports and Household, S's Settings, I's
/// Insights) shows as soon as it is merged in, and shows its name until then.
/// </summary>
internal sealed class AeroPageHost : Grid
{
    public static readonly DependencyProperty ContentProperty = DependencyProperty.Register(
        nameof(Content), typeof(object), typeof(AeroPageHost), new PropertyMetadata(null, (d, _) => ((AeroPageHost)d).Show()));

    public static readonly DependencyProperty PageProperty = DependencyProperty.Register(
        nameof(Page), typeof(Page), typeof(AeroPageHost), new PropertyMetadata(Page.Dashboard, (d, _) => ((AeroPageHost)d).Show()));

    private const double LeaveMs = 150;
    private const double ArriveMs = 450;
    private FrameworkElement? _showing;
    private (string View, object? Content) _shown;

    public object? Content { get => GetValue(ContentProperty); set => SetValue(ContentProperty, value); }

    public Page Page { get => (Page)GetValue(PageProperty); set => SetValue(PageProperty, value); }

    /// <summary>The view on show, for a test.</summary>
    internal FrameworkElement? Showing => _showing;

    /// <summary>The view each page shows, by its full name.</summary>
    public static string ViewName(Page page) => page switch
    {
        Page.Parts => "PowerLedger.App.Aero.PartsView",
        Page.Breakdown => "PowerLedger.App.Aero.HistoryView",
        Page.Report => "PowerLedger.App.Aero.ReportsView",
        Page.Household => "PowerLedger.App.Aero.HouseholdView",
        Page.Settings => "PowerLedger.App.Aero.SettingsView",
        Page.Insights => "PowerLedger.App.Aero.InsightsView",
        _ => "PowerLedger.App.Aero.DashboardView",
    };

    /// <summary>The view for <paramref name="content"/> on <paramref name="page"/>: the wizard's own while it has the
    /// window, the page's while the content is the page's own ViewModel, and otherwise the view of what the shell shows in
    /// its place (the Dashboard for Insights without an InsightsViewModel).</summary>
    public static string ViewNameFor(Page page, object content) => content switch
    {
        WizardViewModel => typeof(WizardView).FullName!,
        DashboardViewModel => ViewName(page == Page.Parts ? Page.Parts : Page.Dashboard),
        BreakdownViewModel => ViewName(Page.Breakdown),
        ReportViewModel => ViewName(Page.Report),
        HouseholdViewModel => ViewName(Page.Household),
        SettingsViewModel => ViewName(Page.Settings),
        InsightsViewModel => ViewName(Page.Insights),
        _ => ViewName(Page.Dashboard),
    };

    private void Show()
    {
        if (Content is not { } content)
        {
            Children.Clear();
            _showing = null;
            _shown = default;
            return;
        }
        var name = ViewNameFor(Page, content);
        if (_shown.View == name && ReferenceEquals(_shown.Content, content)) return;
        _shown = (name, content);
        var arriving = Create(name, Page);
        arriving.DataContext = content;
        var leaving = _showing;
        _showing = arriving;
        Children.Add(arriving);
        if (leaving is null) return;   // the first page comes in with the window's intro, not a transition
        leaving.IsHitTestVisible = false;
        AeroMotion.Fade(leaving, OpacityProperty, 0, LeaveMs, AeroMotion.Glide, done: () => Children.Remove(leaving));
        var rise = new TranslateTransform();
        arriving.RenderTransform = rise;
        AeroMotion.Fade(arriving, OpacityProperty, 1, ArriveMs, AeroMotion.Glide, from: 0);
        AeroMotion.Move(rise, TranslateTransform.YProperty, 0, ArriveMs, AeroMotion.Glide, from: AeroMotion.ContentRise);
    }

    /// <summary>The view by name, or the page's name in its place while its view isn't in this build.</summary>
    private static FrameworkElement Create(string name, Page page)
    {
        if (typeof(AeroPageHost).Assembly.GetType(name) is { } type && Activator.CreateInstance(type) is FrameworkElement view) return view;
        var title = new TextBlock { Text = Title(page) };
        title.SetResourceReference(StyleProperty, "A.Text.Title");
        return new Border { Child = title, Padding = new Thickness(8), VerticalAlignment = VerticalAlignment.Top, Tag = "Placeholder" };
    }

    /// <summary>A page's name as Aero's sidebar gives it.</summary>
    public static string Title(Page page) => page switch
    {
        Page.Now or Page.Dashboard => "Dashboard",
        Page.Breakdown => "History",
        Page.Parts => "Parts",
        Page.Insights => "Insights",
        Page.Report => "Reports",
        Page.Household => "Household",
        _ => "Settings",
    };
}
