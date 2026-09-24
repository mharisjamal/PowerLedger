using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using PowerLedger.Contracts;
using Shouldly;

namespace PowerLedger.App.Tests;

/// <summary>
/// Every keyed Midnight style applied to a control of its type, laid out and drawn in both themes: a template that names a
/// missing part or binds a wrong type fails here, not in a page. The sheet is saved for a person to look at beside the
/// page renders.
/// </summary>
[Trait("Category", "UI")]
public class MidnightStyleSheetTests
{
    [Theory]
    [InlineData("Dark")]
    [InlineData("Light")]
    public void Every_style_applies_and_the_sheet_draws(string themeName)
    {
        var theme = Enum.Parse<Theme>(themeName);
        Directory.CreateDirectory(RenderingTests.Folder);
        var path = System.IO.Path.Combine(RenderingTests.Folder, $"midnight-styles-{theme}.png");
        Sta.Run(() =>
        {
            var styles = MidnightStylesTests.Load();
            var host = new Border { Width = 720, Padding = new Thickness(24) };
            host.Resources.MergedDictionaries.Add(ThemeManager.Palette(Look.Midnight, theme));
            host.Resources.MergedDictionaries.Add(styles);
            host.SetResourceReference(Border.BackgroundProperty, "M.Ground");
            host.Child = Sheet(styles);
            host.Measure(new Size(720, double.PositiveInfinity));
            host.Arrange(new Rect(host.DesiredSize));
            host.UpdateLayout();
            host.ActualHeight.ShouldBeGreaterThan(400);

            var bitmap = new RenderTargetBitmap((int)host.ActualWidth, (int)host.ActualHeight, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(host);
            var png = new PngBitmapEncoder();
            png.Frames.Add(BitmapFrame.Create(bitmap));
            using var file = File.Create(path);
            png.Save(file);
            return true;
        });
        new FileInfo(path).Length.ShouldBeGreaterThan(10_000);
    }

    private static StackPanel Sheet(ResourceDictionary styles)
    {
        Style S(string key) => (Style)styles[key];
        var sheet = new StackPanel();
        void Row(params UIElement[] items)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 16) };
            foreach (var item in items)
            {
                if (item is FrameworkElement element) element.Margin = new Thickness(0, 0, 12, 0);
                row.Children.Add(item);
            }
            sheet.Children.Add(row);
        }
        TextBlock Text(string style, string text) => new() { Style = S(style), Text = text, VerticalAlignment = VerticalAlignment.Center };

        Row(Text("M.Text.Display", "1.24"), Text("M.Text.Title", "Dashboard"), Text("M.Text.Heading", "Power over time"), Text("M.Text.Body", "Body text"),
            Text("M.Text.Secondary", "Secondary"), Text("M.Text.Muted", "Muted"), Text("M.Text.Eyebrow", "POWER NOW"), Text("M.Text.Number", "92 W"));
        Row(new Border { Style = S("M.Card"), Width = 200, Child = Text("M.Text.Body", "Card") },
            new Border { Style = S("M.Card.Flat"), Width = 200, Child = Text("M.Text.Body", "Flat card") },
            new Border { Style = S("M.Glass"), Width = 200, Padding = new Thickness(12), Child = new Grid { Children = { new Rectangle { Style = S("M.Glass.Highlight") }, Text("M.Text.Body", "Glass") } } });

        var nav = new Grid { Width = 232 };
        var pill = new Border { Style = S("M.NavPill"), Margin = new Thickness(12, 0, 12, 0), VerticalAlignment = VerticalAlignment.Top };
        nav.Children.Add(pill);
        var items = new StackPanel();
        items.Children.Add(new TextBlock { Style = S("M.NavGroup"), Text = "OVERVIEW" });
        items.Children.Add(new RadioButton { Style = S("M.NavItem"), Content = "Dashboard", Tag = "", IsChecked = true });
        items.Children.Add(new RadioButton { Style = S("M.NavItem"), Content = "History", Tag = "" });
        nav.Children.Add(items);
        Row(nav, new ContentControl { Style = S("M.Badge"), Content = 2 }, new ContentControl { Style = S("M.Badge"), Content = 0 });

        Row(new Button { Style = S("M.Button.Primary"), Content = "Add a PC" }, new Button { Style = S("M.Button.Outline"), Content = "Export CSV" },
            new Button { Style = S("M.Button.Quiet"), Content = "Later" }, new Button { Style = S("M.IconButton"), Content = "" });

        var track = new Border { Style = S("M.PillTrack") };
        var pills = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var (label, on) in new[] { ("1H", false), ("1D", true), ("1W", false) }) pills.Children.Add(new RadioButton { Style = S("M.Pill"), Content = label, IsChecked = on });
        track.Child = pills;
        Row(track, new ToggleButton { Style = S("M.Switch"), Content = "Share my figures", IsChecked = true }, new ToggleButton { Style = S("M.Switch"), Content = "Off" },
            new TextBox { Style = S("M.Field"), Text = "0.28", Width = 90 }, new CheckBox { Style = S("M.Tick"), Content = "Include my household", IsChecked = true });

        Row(new ContentControl { Style = S("M.Chip.Measured") }, new ContentControl { Style = S("M.Chip.Calibrated") }, new ContentControl { Style = S("M.Chip.Estimated") },
            new QualityBadge { Style = S("M.Chip"), Quality = Quality.Calibrated }, new QualityBadge { Style = S("M.Chip"), Quality = Quality.Estimated },
            new ContentControl { Style = S("M.StatusPill.Good"), Content = "Recording" }, new ContentControl { Style = S("M.StatusPill.Warn"), Content = "Estimating" },
            new ContentControl { Style = S("M.StatusPill.Bad"), Content = "Service not running" });

        var table = new StackPanel { Width = 400 };
        table.Children.Add(new Border { Style = S("M.Table.Header"), Child = Text("M.Text.Eyebrow", "PART") });
        foreach (var name in new[] { "CPU", "GPU" })
        {
            var cells = new DockPanel();
            var share = new Border { Style = S("M.ShareTrack"), Width = 120 };
            DockPanel.SetDock(share, Dock.Right);
            cells.Children.Add(share);
            cells.Children.Add(Text("M.Text.Body", name));
            table.Children.Add(new Border { Style = S("M.Table.Row"), Child = cells });
        }
        Row(table);
        return sheet;
    }
}
