using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using PowerLedger.App.Aero;
using PowerLedger.Contracts;

namespace PowerLedger.App.Tests;

/// <summary>
/// 0.10.9's gap audit: the mockup's own sample figures put into an App render (146 W, 1.84 kWh, +12%, $5.73, the fourteen
/// days, the four parts, This PC and Study PC), so a proof's words and figures are the board's letter for letter and each
/// one's ink can be measured against Edge's. Only the words and numbers change: every piece, style and layout is the
/// App's own. Proof only (PL_PROOF_DIR).
/// </summary>
internal static class MockupData
{
    /// <summary>The mockup's fourteen days (Main.dc.html's renderVals), oldest first.</summary>
    public static readonly double[] Days = [62, 88, 74, 120, 96, 140, 110, 82, 130, 152, 118, 166, 144, 190];

    /// <summary>The mockup's Last minute path, its 19 points' heights in its 400 by 180 view box.</summary>
    public static readonly double[] LineY = [126, 116, 128, 100, 110, 92, 104, 76, 88, 70, 94, 80, 60, 74, 50, 64, 42, 58, 46];

    public static void Apply(AeroWindow window)
    {
        if (window.FindName("Pcs") is ItemsControl pcs)
            pcs.ItemsSource = new List<PcRow> { new("TP", "This PC", "146 W", true, .7, "$4.02"), new("SP", "Study PC", "61 W", false, .3, "$1.71") };
        if (window.PageHost.Showing is PowerLedger.App.Aero.DashboardView view) Dashboard(view);
        if (window.PageHost.Showing is PowerLedger.App.Aero.SettingsView settings)
        {
            foreach (var card in settings.Cards) card.Watts = "146";
        }
        window.UpdateLayout();
    }

    private static void Dashboard(PowerLedger.App.Aero.DashboardView view)
    {
        T N<T>(string name) where T : class => (T)view.FindName(name);
        N<RollingNumber>("NowRoll").Value = 146;
        N<TextBlock>("NowSource").Text = "Measured by the CPU and GPU sensors";
        N<TextBlock>("TodayKwh").Text = "1.84";
        N<TextBlock>("ChangeText").Text = "+12%";
        N<TrackedText>("MonthBig").Text = "$5.73";
        N<TextBlock>("MonthSub").Text = "38.2 kWh so far, on track for $6.90";
        typeof(PowerLedger.App.Aero.DashboardView).GetField("_monthFill", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(view, .82);
        var bar = N<Grid>("MonthBar");
        N<Grid>("MonthFill").Width = bar.ActualWidth * .82;
        N<ItemsControl>("MonthSplit").ItemsSource = new List<PcRow> { new("TP", "This PC", "146 W", true, .7, "$4.02"), new("SP", "Study PC", "61 W", false, .3, "$1.71") };
        var today = new DateOnly(2026, 9, 14);
        N<EnergyBarChart>("Bars").Bars = [.. Days.Select((kwh, i) => new EnergyBar(today.AddDays(i - 13), (i + 1).ToString(System.Globalization.CultureInfo.InvariantCulture), kwh, ""))];
        (Part Part, string Name, string Model, double Share)[] parts =
        [
            (Part.Cpu, "CPU package", "Intel Core i7-1165G7", .46), (Part.Gpu, "Graphics", "NVIDIA GeForce MX330", .28),
            (Part.Display, "Display", "DELL U2720Q", .17), (Part.Rest, "Rest of system", "16 GB RAM, NVMe 512 GB", .09),
        ];
        N<DonutChart>("Ring").Parts = [.. parts.Select(p => new DashboardPart(p.Part, p.Name, "", "", "", p.Share, null, null, TrendKind.Flat) { Model = p.Model })];
        N<ItemsControl>("PartsList").ItemsSource = parts.Select(p => new PowerLedger.App.Aero.PartRow(p.Part, p.Name, p.Model, $"{p.Share * 100:0}%", Glow(view, p.Part))).ToList();
        var live = N<LiveChart>("Live");
        BindingOperationsClear(live);
        live.Average = double.NaN;
        live.Samples = [.. LineY.Select((y, i) => new SparkSample((LineY.Length - 1 - i) * 60.0 / (LineY.Length - 1), 140 - y))];
    }

    private static void BindingOperationsClear(FrameworkElement e)
    {
        System.Windows.Data.BindingOperations.ClearBinding(e, LiveChart.SamplesProperty);
        System.Windows.Data.BindingOperations.ClearBinding(e, LiveChart.AverageProperty);
    }

    private static Effect Glow(FrameworkElement view, Part part)
    {
        var key = part switch { Part.Cpu => "M.PartCpu", Part.Gpu => "M.PartGpu", Part.Display => "M.PartDisplay", _ => "M.PartRest" };
        var colour = view.TryFindResource(key) is SolidColorBrush brush ? brush.Color : Colors.White;
        return new DropShadowEffect { ShadowDepth = 0, BlurRadius = 8, Opacity = 1, Color = colour, RenderingBias = RenderingBias.Performance };
    }

    /// <summary>The kit's watts pill reading: 146 and the kit's sparkline (its path's ten heights of 26, oldest first).</summary>
    public static void Overlay(OverlayWindow overlay)
    {
        ((RollingNumber)overlay.FindName("Number")).Value = 146;
        double[] heights = [18, 16, 19, 12, 14, 9, 13, 6, 10, 7];
        var spark = (PowerLedger.App.Aero.Sparkline)overlay.FindName("Spark");
        System.Windows.Data.BindingOperations.ClearAllBindings(spark);
        spark.SetResourceReference(PowerLedger.App.Aero.Sparkline.StrokeProperty, "A.B.AccentHigh");
        spark.Seconds = 30;
        spark.Samples = [.. heights.Select((y, i) => new SparkSample((heights.Length - 1 - i) * 30.0 / (heights.Length - 1), 26 - y))];
        overlay.UpdateLayout();
    }
}
