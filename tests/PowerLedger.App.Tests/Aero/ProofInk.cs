using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PowerLedger.App.Aero;

namespace PowerLedger.App.Tests;

/// <summary>
/// 0.10.9's gap audit: what the proofs hand the comparison beside each render. The render again with the text hidden and
/// with the text and icons hidden (the difference is each one's ink, as the mockup's are found in Edge), and every text
/// run, glass piece and icon with its box in the window and what it is drawn with, as JSON. Proof only (PL_PROOF_DIR).
/// </summary>
internal static class ProofInk
{
    /// <summary>Writes <paramref name="stem"/>-notext.png, -noink.png and -dom.json for <paramref name="root"/> drawn over
    /// <paramref name="ground"/>; the text and icons are shown again after.</summary>
    public static void Write(BitmapSource ground, FrameworkElement root, Visual drawn, int width, int height, string stem)
    {
        File.WriteAllText(stem + "-dom.json", Dump(root));
        var hidden = new List<(UIElement Element, double Opacity)>();
        void Hide(Func<DependencyObject, bool> which)
        {
            foreach (var e in All(root).Where(which).OfType<UIElement>())
            {
                if (hidden.Any(h => h.Element == e)) continue;
                hidden.Add((e, e.Opacity));
                e.Opacity = 0;
            }
            UiHarness.Pump(TimeSpan.FromMilliseconds(300));
            root.UpdateLayout();
        }
        try
        {
            Hide(e => e is TextBlock or TrackedText);
            LiquidGlassProofTests.Write(LiquidGlassProofTests.Over(ground, drawn, width, height), stem + "-notext.png");
            Hide(e => e is Icon);
            LiquidGlassProofTests.Write(LiquidGlassProofTests.Over(ground, drawn, width, height), stem + "-noink.png");
        }
        finally
        {
            foreach (var (e, opacity) in hidden) e.Opacity = opacity;
            UiHarness.Pump(TimeSpan.FromMilliseconds(300));
        }
    }

    /// <summary>Every visible text run, glass piece and icon under <paramref name="root"/>, with its box in it.</summary>
    public static string Dump(FrameworkElement root)
    {
        var texts = new List<string>();
        var pieces = new List<string>();
        var icons = new List<string>();
        var wells = new List<string>();
        var discs = new List<string>();
        foreach (var e in All(root).OfType<FrameworkElement>())
        {
            if (!e.IsVisible || e.ActualWidth <= 0 || e.ActualHeight <= 0 || Opacity(e, root) <= 0) continue;
            Rect box;
            try { box = e.TransformToAncestor(root).TransformBounds(new Rect(e.RenderSize)); }
            catch (InvalidOperationException) { continue; }
            var at = F($"\"x\":{box.X:0.##},\"y\":{box.Y:0.##},\"w\":{box.Width:0.##},\"h\":{box.Height:0.##},\"opacity\":{Opacity(e, root):0.###}");
            if (e is TextBlock && InRoll(e)) continue;
            switch (e)
            {
                case RollingNumber r:
                    texts.Add(F($"{{\"text\":{Json(r.Text)},{at},\"font\":{Json(System.Windows.Documents.TextElement.GetFontFamily(r).Source)},\"size\":{r.DigitSize:0.##},\"weight\":{r.DigitWeight.ToOpenTypeWeight()},\"color\":{Json(Colour(System.Windows.Documents.TextElement.GetForeground(r)))},\"shadow\":true,\"name\":{Json(r.Name)}}}"));
                    break;
                case TextBlock t when !string.IsNullOrWhiteSpace(t.Text):
                    texts.Add(F($"{{\"text\":{Json(t.Text)},{at},\"font\":{Json(t.FontFamily.Source)},\"size\":{t.FontSize:0.##},\"weight\":{t.FontWeight.ToOpenTypeWeight()},\"color\":{Json(Colour(t.Foreground))},\"shadow\":{(t.Effect is not null ? "true" : "false")},\"name\":{Json(t.Name)}}}"));
                    break;
                case TrackedText k when !string.IsNullOrWhiteSpace(k.Text):
                    texts.Add(F($"{{\"text\":{Json(k.Text)},{at},\"font\":{Json(k.FontFamily.Source)},\"size\":{k.FontSize:0.##},\"weight\":{k.FontWeight.ToOpenTypeWeight()},\"color\":{Json(Colour(k.Foreground))},\"shadow\":{(k.Effect is not null ? "true" : "false")},\"name\":{Json(k.Name)}}}"));
                    break;
                case GlassPanel g:
                    pieces.Add(F($"{{\"name\":{Json(g.Name)},{at},\"radius\":{g.CornerRadius.TopLeft:0.##},\"tint\":{Json(Colour(g.Background))},\"bubble\":{(g.Bubble ? "true" : "false")},\"shadow\":{(g.HasShadow ? "true" : "false")},\"nested\":{(g.IsNested ? "true" : "false")}}}"));
                    break;
                case Icon i:
                    icons.Add(F($"{{{at},\"size\":{i.Size:0.##},\"stroke\":{i.StrokeWidth:0.##},\"filled\":{(i.Filled ? "true" : "false")},\"color\":{Json(Colour(i.Foreground))}}}"));
                    break;
                case Border b when b.Background is not null && Colour(b.Background) is not ("" or "#00FFFFFF" or "#00000000") && b.CornerRadius.TopLeft > 0:
                    wells.Add(F($"{{\"name\":{Json(b.Name)},{at},\"radius\":{b.CornerRadius.TopLeft:0.##},\"fill\":{Json(Colour(b.Background))},\"edge\":{Json(Colour(b.BorderBrush))},\"edgeWidth\":{b.BorderThickness.Top:0.##}}}"));
                    break;
                case System.Windows.Shapes.Ellipse el when el.Fill is not null:
                    discs.Add(F($"{{\"name\":{Json(el.Name)},{at},\"fill\":{Json(Colour(el.Fill))}}}"));
                    break;
            }
        }
        return "{\"texts\":[" + string.Join(",\n", texts) + "],\n\"pieces\":[" + string.Join(",\n", pieces) + "],\n\"icons\":[" + string.Join(",\n", icons)
            + "],\n\"wells\":[" + string.Join(",\n", wells) + "],\n\"discs\":[" + string.Join(",\n", discs) + "]}";
    }

    private static bool InRoll(DependencyObject e)
    {
        for (var n = VisualTreeHelper.GetParent(e); n is not null; n = VisualTreeHelper.GetParent(n))
            if (n is RollingNumber) return true;
        return false;
    }

    private static double Opacity(DependencyObject e, DependencyObject root)
    {
        var o = 1.0;
        for (var n = e; n is not null && n != root; n = VisualTreeHelper.GetParent(n))
            if (n is UIElement u) o *= u.Opacity;
        return o;
    }

    internal static IEnumerable<DependencyObject> All(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            yield return child;
            foreach (var deeper in All(child)) yield return deeper;
        }
    }

    private static string Colour(Brush? brush) => brush switch
    {
        SolidColorBrush s => $"#{(byte)Math.Round(s.Color.A * s.Opacity):X2}{s.Color.R:X2}{s.Color.G:X2}{s.Color.B:X2}",
        GradientBrush g => string.Join(" ", g.GradientStops.Select(s => $"#{s.Color.A:X2}{s.Color.R:X2}{s.Color.G:X2}{s.Color.B:X2}@{s.Offset.ToString("0.##", CultureInfo.InvariantCulture)}")),
        null => "",
        _ => brush.GetType().Name,
    };

    private static string Json(string s)
    {
        var b = new StringBuilder("\"");
        foreach (var c in s) b.Append(c switch { '"' => "\\\"", '\\' => "\\\\", '\n' => "\\n", '\r' => "", _ when c < ' ' || c > '~' => $"\\u{(int)c:x4}", _ => c.ToString() });
        return b.Append('"').ToString();
    }

    private static string F(FormattableString s) => s.ToString(CultureInfo.InvariantCulture);
}
