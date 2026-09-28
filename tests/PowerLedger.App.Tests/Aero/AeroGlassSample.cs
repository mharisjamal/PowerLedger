using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using PowerLedger.App.Aero;

namespace PowerLedger.App.Tests;

/// <summary>
/// A sample of Aero's glass system for the render tests (Plan S G1 to G3): a window dressed with Aero's palette and
/// styles (AeroHost.Dressed, review 11) over a scene like the demo's fallback wallpaper, holding a sidebar, a pane of
/// figures and controls, a table, a dialog and a toast, so a PNG shows every style of the contract together.
/// </summary>
internal static class AeroGlassSample
{
    public const int Width = 1100;
    public const int Height = 640;

    /// <summary>The demo's "Wallpaper" scene (the prototype's FallbackScene): deep navy with blue, violet and cyan glows;
    /// for the light theme, a pale sky with the same glows washed out.</summary>
    public static Brush Scene(Theme theme)
    {
        var group = new DrawingGroup();
        var bounds = new Rect(0, 0, 1, 1);
        var dark = theme == Theme.Dark;
        group.Children.Add(new GeometryDrawing(new SolidColorBrush(dark ? Color.FromRgb(0x0a, 0x0f, 0x1f) : Color.FromRgb(0xdd, 0xe4, 0xf2)), null, new RectangleGeometry(bounds)));
        void Glow(double x, double y, double rx, double ry, Color c)
        {
            var brush = new RadialGradientBrush(c, Color.FromArgb(0, c.R, c.G, c.B)) { Center = new Point(x, y), GradientOrigin = new Point(x, y), RadiusX = rx, RadiusY = ry };
            group.Children.Add(new GeometryDrawing(brush, null, new RectangleGeometry(bounds)));
        }
        Glow(.28, .42, .4, .55, dark ? Color.FromArgb(140, 64, 110, 255) : Color.FromArgb(120, 120, 160, 255));
        Glow(.70, .60, .34, .48, dark ? Color.FromArgb(107, 150, 80, 255) : Color.FromArgb(90, 190, 150, 255));
        Glow(.62, .18, .26, .36, dark ? Color.FromArgb(71, 0, 200, 255) : Color.FromArgb(80, 120, 220, 255));
        var scene = new DrawingBrush(group) { Stretch = Stretch.Fill };
        scene.Freeze();
        return scene;
    }

    /// <summary>The sample window, off screen and unactivated; the caller shows, renders and closes it.</summary>
    public static Window Window(Theme theme)
    {
        var window = AeroHost.Dressed(new Window
        {
            Width = Width, Height = Height, WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.Manual, Left = -20000, Top = 0, ShowInTaskbar = false, ShowActivated = false,
            Background = Scene(theme),
        }, theme);
        window.SetResourceReference(Control.FontFamilyProperty, "A.F.Ui");
        var root = new Grid { Margin = new Thickness(24) };
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(240) });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(18) });
        root.ColumnDefinitions.Add(new ColumnDefinition());
        root.Children.Add(Sidebar(window));
        var main = new Grid();
        Grid.SetColumn(main, 2);
        main.RowDefinitions.Add(new RowDefinition { Height = new GridLength(250) });
        main.RowDefinitions.Add(new RowDefinition { Height = new GridLength(16) });
        main.RowDefinitions.Add(new RowDefinition());
        main.Children.Add(Controls(window));
        var table = Table(window);
        Grid.SetRow(table, 2);
        main.Children.Add(table);
        root.Children.Add(main);
        var layer = new Grid();
        layer.Children.Add(new Border { Name = "Scene", Background = Scene(theme), IsHitTestVisible = false });
        layer.Children.Add(root);
        var modal = new GlassPanel { Style = Keyed(window, "A.Modal"), Width = 330, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 44, 44) };
        var body = new StackPanel();
        body.Children.Add(Text(window, "A.Text.Title", "September report"));
        body.Children.Add(Text(window, "A.Label", "1 to 25 September, both PCs"));
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        actions.Children.Add(new Button { Style = Keyed(window, "A.GhostBtn"), Content = "Close", Margin = new Thickness(0, 0, 8, 0) });
        actions.Children.Add(new Button { Style = Keyed(window, "A.AccentBtn"), Content = "Save as PDF" });
        body.Children.Add(actions);
        modal.Content = body;
        layer.Children.Add(modal);
        var toast = new GlassPanel { Style = Keyed(window, "A.Toast"), Margin = new Thickness(0, 0, 0, 30) };
        toast.Content = Text(window, "A.Text.Body", "Saved day history to Documents");
        layer.Children.Add(toast);
        window.Content = layer;
        return window;
    }

    /// <summary>The sample's scene layer, behind everything: where a backdrop puts the wash, the wallpaper or the ground.</summary>
    public static Border SceneOf(Window window) => (Border)((Grid)window.Content).Children[0];

    private static GlassPanel Sidebar(Window window)
    {
        var side = new GlassPanel { Padding = new Thickness(14, 18, 14, 18) };
        var stack = new StackPanel();
        var brand = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(6, 2, 6, 20) };
        var mark = new Grid { Width = 32, Height = 32 };
        var disc = new System.Windows.Shapes.Ellipse();
        disc.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, "A.B.Accent");
        mark.Children.Add(disc);
        var bolt = new PowerLedger.App.Aero.Icon { Data = (System.Windows.Media.Geometry)window.FindResource("A.I.Bolt"), Filled = true };
        bolt.SetResourceReference(PowerLedger.App.Aero.Icon.ForegroundProperty, "A.B.AccentInk");
        mark.Children.Add(bolt);
        brand.Children.Add(mark);
        var name = Text(window, "A.Text.Title", "PowerLedger");
        name.Margin = new Thickness(10, 0, 0, 0);
        name.VerticalAlignment = VerticalAlignment.Center;
        brand.Children.Add(name);
        stack.Children.Add(brand);
        foreach (var (page, icon, on) in new[] { ("Dashboard", "A.I.Dashboard", true), ("History", "A.I.History", false), ("Parts", "A.I.Chip", false), ("Insights", "A.I.Insights", false), ("Settings", "A.I.Gear", false) })
        {
            stack.Children.Add(new RadioButton
            {
                Style = Keyed(window, "A.NavItem"), Content = page, Tag = window.FindResource(icon), IsChecked = on, GroupName = "nav", Margin = new Thickness(0, 0, 0, 9),
            });
        }
        var group = Text(window, "A.Text.Muted", "Your PCs");
        group.FontSize = 12.5;
        group.Margin = new Thickness(8, 12, 8, 8);
        stack.Children.Add(group);
        stack.Children.Add(new Button { Style = Keyed(window, "A.RowBtn"), Content = Text(window, "A.Text.Body", "This PC") });
        side.Content = stack;
        return side;
    }

    private static GlassPanel Controls(Window window)
    {
        var pane = new GlassPanel { Padding = new Thickness(20, 18, 20, 18) };
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(250) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(14) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        var well = new Border { Style = Keyed(window, "A.Well") };
        var figures = new StackPanel();
        figures.Children.Add(Text(window, "A.PanelTitle", "Power now"));
        var big = Text(window, "A.Text.Big", "142 W");
        big.Margin = new Thickness(0, 30, 0, 0);
        figures.Children.Add(big);
        figures.Children.Add(Text(window, "A.Text.Mid", "1.84"));
        figures.Children.Add(Text(window, "A.Label", "kWh used today"));
        figures.Children.Add(Text(window, "A.Text.Axis", "12:04  12:05  12:06"));
        well.Child = figures;
        grid.Children.Add(well);
        var controls = new StackPanel();
        Grid.SetColumn(controls, 2);
        controls.Children.Add(Row(new Button { Style = Keyed(window, "A.GlassBtn"), Content = "Glass" },
            new Button { Style = Keyed(window, "A.OutlineBtn"), Content = "September" },
            new Button { Style = Keyed(window, "A.AccentBtn"), Content = "Open report" },
            new Button { Style = Keyed(window, "A.RoundGlassBtn"), Content = Glyph(window, "A.I.Bell") },
            new Button { Style = Keyed(window, "A.WhiteRoundBtn"), Content = Glyph(window, "A.I.Unit", filled: true) },
            new Button { Style = Keyed(window, "A.ExpandBtn"), Content = Glyph(window, "A.I.Expand") }));
        controls.Children.Add(Row(new System.Windows.Controls.Primitives.ToggleButton { Style = Keyed(window, "A.GlassToggle"), Content = "Show overlay", IsChecked = true },
            new RadioButton { Style = Keyed(window, "A.OptItem"), Content = "Tinted", IsChecked = true, GroupName = "opt" },
            new RadioButton { Style = Keyed(window, "A.OptItem"), Content = "Clear", GroupName = "opt" },
            new RadioButton { Style = Keyed(window, "A.TextToggle"), Content = "Cost", IsChecked = true, GroupName = "tt", Margin = new Thickness(8, 0, 12, 0) },
            new RadioButton { Style = Keyed(window, "A.TextToggle"), Content = "Energy", GroupName = "tt" }));
        controls.Children.Add(Row(new GlassSwitch { IsChecked = true }, new GlassSwitch(), new CheckBox { Content = "Reduce motion", IsChecked = true, Margin = new Thickness(12, 0, 12, 0) },
            new CheckBox { Content = "Parallax" }));
        controls.Children.Add(Row(new Slider { Style = Keyed(window, "A.Slider"), Width = 180, Minimum = 0, Maximum = 1, Value = 0.6 },
            new TextBox { Text = "412", Width = 90, Margin = new Thickness(12, 0, 0, 0) }));
        var seg = new Border { Height = 42, Padding = new Thickness(4), CornerRadius = new CornerRadius(21), BorderThickness = new Thickness(1), HorizontalAlignment = HorizontalAlignment.Left };
        seg.SetResourceReference(Border.BorderBrushProperty, "A.B.Line");
        var segItems = new Grid();
        var pill = new Border { Width = 70, Height = 32, CornerRadius = new CornerRadius(16), HorizontalAlignment = HorizontalAlignment.Left };
        pill.SetResourceReference(Border.BackgroundProperty, "A.B.Pill");
        segItems.Children.Add(pill);
        var segRow = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var (span, on) in new[] { ("Day", true), ("Week", false), ("Month", false) })
            segRow.Children.Add(new RadioButton { Style = Keyed(window, "A.SegItem"), Content = span, IsChecked = on, GroupName = "seg" });
        segItems.Children.Add(segRow);
        seg.Child = segItems;
        controls.Children.Add(seg);
        grid.Children.Add(controls);
        pane.Content = grid;
        return pane;
    }

    private static GlassPanel Table(Window window)
    {
        var pane = new GlassPanel { Padding = new Thickness(20, 18, 20, 12) };
        var stack = new StackPanel();
        stack.Children.Add(Text(window, "A.PanelTitle", "History"));
        var head = new Border { Style = Keyed(window, "A.Table.Header"), Margin = new Thickness(0, 12, 0, 2) };
        head.Child = Cells("Day", "Energy", "Cost", null);
        stack.Children.Add(head);
        foreach (var (day, kwh, cost, chip) in new[] { ("Today", "1.84 kWh", "$0.28", "A.Chip.Measured"), ("Yesterday", "2.02 kWh", "$0.30", "A.Chip.Calibrated"), ("Tuesday", "1.66 kWh", "$0.25", "A.Chip.Estimated") })
        {
            var row = new Border { Style = Keyed(window, "A.Table.Row") };
            row.Child = Cells(day, kwh, cost, new ContentControl { Style = Keyed(window, chip) });
            stack.Children.Add(row);
        }
        pane.Content = stack;
        return pane;
    }

    private static Grid Cells(string a, string b, string c, UIElement? last)
    {
        var grid = new Grid();
        for (var i = 0; i < 4; i++) grid.ColumnDefinitions.Add(new ColumnDefinition());
        var cells = new UIElement[] { new TextBlock { Text = a }, new TextBlock { Text = b }, new TextBlock { Text = c }, last ?? new TextBlock { Text = "Source" } };
        for (var i = 0; i < cells.Length; i++)
        {
            Grid.SetColumn(cells[i], i);
            grid.Children.Add(cells[i]);
        }
        return grid;
    }

    private static StackPanel Row(params FrameworkElement[] items)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 12) };
        foreach (var item in items)
        {
            if (item.Margin == default) item.Margin = new Thickness(0, 0, 8, 0);
            item.VerticalAlignment = VerticalAlignment.Center;
            row.Children.Add(item);
        }
        return row;
    }

    private static PowerLedger.App.Aero.Icon Glyph(Window window, string key, bool filled = false)
        => new() { Data = (System.Windows.Media.Geometry)window.FindResource(key), Filled = filled, Size = 16 };

    private static TextBlock Text(Window window, string style, string text) => new() { Style = Keyed(window, style), Text = text };

    private static Style Keyed(Window window, string key) => (Style)window.FindResource(key);
}
