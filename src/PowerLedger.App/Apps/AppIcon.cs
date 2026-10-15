using System.Collections.Concurrent;
using System.ComponentModel;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace PowerLedger.App;

/// <summary>
/// The icons of programs (Plan AE), each read once from its file off the UI thread and kept for the run, frozen so any
/// thread may draw it. A file that is gone, unreadable or has no icon is remembered as having none.
/// </summary>
internal sealed class AppIcons(Func<string, ImageSource?> read)
{
    /// <summary>How large an icon is read: crisp at the ledger's size up to 175 % scaling.</summary>
    public const int ReadSize = 48;

    private readonly ConcurrentDictionary<string, Task<ImageSource?>> _icons = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The App's own, reading program files; a test may stand another in.</summary>
    public static AppIcons Shared { get; set; } = new(Read);

    /// <summary>The icon of the program at <paramref name="path"/>, read once.</summary>
    public Task<ImageSource?> For(string path) => _icons.GetOrAdd(path, p => Task.Run(() =>
    {
        try
        {
            return read(p);
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            return null;
        }
    }));

    /// <summary>The icon a program's file holds, at <see cref="ReadSize"/>; null without one. A program on a network share
    /// keeps its glyph: the ledger never reaches across the network for a picture.</summary>
    internal static ImageSource? Read(string path)
    {
        try
        {
            if (path.StartsWith(@"\\", StringComparison.Ordinal) || path.StartsWith("//", StringComparison.Ordinal) || !File.Exists(path)) return null;
            using var icon = System.Drawing.Icon.ExtractIcon(path, 0, ReadSize);
            if (icon is null) return null;
            using var bitmap = icon.ToBitmap();
            var data = bitmap.LockBits(new System.Drawing.Rectangle(0, 0, bitmap.Width, bitmap.Height), ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            try
            {
                var source = BitmapSource.Create(bitmap.Width, bitmap.Height, 96, 96, PixelFormats.Bgra32, null, data.Scan0, data.Stride * bitmap.Height, data.Stride);
                source.Freeze();
                return source;
            }
            finally
            {
                bitmap.UnlockBits(data);
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or ExternalException or Win32Exception)
        {
            return null;
        }
    }
}

/// <summary>
/// A line's picture in the Apps ledger (Plan AE): a program's own icon, read from its file (<see cref="AppIcons"/>), or,
/// for a Store app, a program without an icon and every line that is no app, a plain glyph on a quiet tile. Drawn, not
/// templated, so a ledger of thirty apps costs one visual each; the glyph takes the foreground, so it follows the ink.
/// To a screen reader it is decoration: the line it is on carries the name.
/// </summary>
internal sealed class AppIcon : FrameworkElement
{
    public static readonly DependencyProperty PathProperty = DependencyProperty.Register(
        nameof(Path), typeof(string), typeof(AppIcon), new FrameworkPropertyMetadata(null, (d, _) => ((AppIcon)d).Load()));

    public static readonly DependencyProperty GlyphProperty = DependencyProperty.Register(
        nameof(Glyph), typeof(AppGlyph), typeof(AppIcon), new FrameworkPropertyMetadata(AppGlyph.Program, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ForegroundProperty = System.Windows.Documents.TextElement.ForegroundProperty.AddOwner(
        typeof(AppIcon), new FrameworkPropertyMetadata(Brushes.Gray, FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.Inherits));

    public static readonly DependencyProperty TileProperty = DependencyProperty.Register(
        nameof(Tile), typeof(Brush), typeof(AppIcon), new FrameworkPropertyMetadata(Brushes.Transparent, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>How thick a glyph's strokes are drawn, in device-independent pixels at any size.</summary>
    public const double Stroke = 1.6;

    /// <summary>How much of the tile the glyph takes.</summary>
    public const double GlyphShare = 0.6;

    private ImageSource? _image;

    public AppIcon()
    {
        Focusable = false;
        RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.HighQuality);
    }

    /// <summary>The program's file; null for a line that is no program.</summary>
    public string? Path { get => (string?)GetValue(PathProperty); set => SetValue(PathProperty, value); }

    public AppGlyph Glyph { get => (AppGlyph)GetValue(GlyphProperty); set => SetValue(GlyphProperty, value); }

    public Brush Foreground { get => (Brush)GetValue(ForegroundProperty); set => SetValue(ForegroundProperty, value); }

    /// <summary>The quiet tile under a glyph.</summary>
    public Brush Tile { get => (Brush)GetValue(TileProperty); set => SetValue(TileProperty, value); }

    /// <summary>The program's icon once read, for a test; null while it shows a glyph.</summary>
    internal ImageSource? Image => _image;

    /// <summary>The glyph's figure on a 24 unit grid, stroked with round ends.</summary>
    internal static System.Windows.Media.Geometry Figure(AppGlyph glyph) => Figures.GetOrAdd(glyph, static g =>
    {
        var data = g switch
        {
            AppGlyph.Store => "M5,8 H19 L18,20 H6 Z M9,8 V6.5 A3,3 0 0 1 15,6.5 V8",
            AppGlyph.Closed => "M5,4.5 H19 A2,2 0 0 1 21,6.5 V17.5 A2,2 0 0 1 19,19.5 H5 A2,2 0 0 1 3,17.5 V6.5 A2,2 0 0 1 5,4.5 Z M3,8.5 H21 M9.5,11.5 L14.5,16.5 M14.5,11.5 L9.5,16.5",
            AppGlyph.Others => "M5,12 A1.2,1.2 0 1 0 5.01,12 Z M12,12 A1.2,1.2 0 1 0 12.01,12 Z M19,12 A1.2,1.2 0 1 0 19.01,12 Z",
            AppGlyph.Users => "M9,11 A3.2,3.2 0 1 0 9,4.6 A3.2,3.2 0 1 0 9,11 Z M3,20 A6,6 0 0 1 15,20 M16,4.8 A3.1,3.1 0 0 1 16,10.8 M17.5,14.3 A6,6 0 0 1 21,20",
            AppGlyph.Untraced => "M12,3 A9,9 0 1 0 12.01,3 Z M12,11 V16.5 M12,7.6 V7.7",
            AppGlyph.Screen => "M4,4 H20 A1.5,1.5 0 0 1 21.5,5.5 V15.5 A1.5,1.5 0 0 1 20,17 H4 A1.5,1.5 0 0 1 2.5,15.5 V5.5 A1.5,1.5 0 0 1 4,4 Z M8,21 H16 M12,17 V21",
            AppGlyph.System => "M12,3 L21,8 L12,13 L3,8 Z M3,12.5 L12,17.5 L21,12.5 M3,16.5 L12,21.5 L21,16.5",
            AppGlyph.Rest => "M7.5,7 H16.5 A0.5,0.5 0 0 1 17,7.5 V16.5 A0.5,0.5 0 0 1 16.5,17 H7.5 A0.5,0.5 0 0 1 7,16.5 V7.5 A0.5,0.5 0 0 1 7.5,7 Z M9.5,2.5 V5.5 M14.5,2.5 V5.5 M9.5,18.5 V21.5 M14.5,18.5 V21.5 M2.5,9.5 H5.5 M2.5,14.5 H5.5 M18.5,9.5 H21.5 M18.5,14.5 H21.5",
            AppGlyph.Total => "",
            _ => "M5,4.5 H19 A2,2 0 0 1 21,6.5 V17.5 A2,2 0 0 1 19,19.5 H5 A2,2 0 0 1 3,17.5 V6.5 A2,2 0 0 1 5,4.5 Z M3,8.5 H21 M6,6.5 H6.01 M8.5,6.5 H8.51",
        };
        var geometry = System.Windows.Media.Geometry.Parse(data);
        geometry.Freeze();
        return geometry;
    });

    private static readonly ConcurrentDictionary<AppGlyph, System.Windows.Media.Geometry> Figures = new();

    protected override Size MeasureOverride(Size availableSize)
        => new(double.IsInfinity(availableSize.Width) ? 28 : availableSize.Width, double.IsInfinity(availableSize.Height) ? 28 : availableSize.Height);

    protected override void OnRender(DrawingContext dc)
    {
        var size = Math.Min(ActualWidth, ActualHeight);
        if (!(size > 0)) return;
        var box = new Rect((ActualWidth - size) / 2, (ActualHeight - size) / 2, size, size);
        if (_image is { } image)
        {
            dc.DrawImage(image, box);
            return;
        }
        if (Glyph == AppGlyph.Total) return;
        var corner = size * 0.3;
        dc.DrawRoundedRectangle(Tile, null, box, corner, corner);
        var scale = size * GlyphShare / 24;
        var inset = (size - 24 * scale) / 2;
        dc.PushTransform(new MatrixTransform(scale, 0, 0, scale, box.X + inset, box.Y + inset));
        dc.DrawGeometry(null, new Pen(Foreground, Stroke / scale) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round }, Figure(Glyph));
        dc.Pop();
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new Decoration(this);

    /// <summary>The icon for <see cref="Path"/>: at once when it has been read before, else when it has been read, if the
    /// line still shows the same program then.</summary>
    private void Load()
    {
        var path = Path;
        _image = null;
        InvalidateVisual();
        if (string.IsNullOrEmpty(path)) return;
        var icon = AppIcons.Shared.For(path);
        if (icon.IsCompletedSuccessfully)
        {
            _image = icon.Result;
            return;
        }
        icon.ContinueWith(read => Dispatcher.BeginInvoke(() =>
        {
            if (Path != path) return;
            _image = read.IsCompletedSuccessfully ? read.Result : null;
            InvalidateVisual();
        }, DispatcherPriority.Background), TaskScheduler.Default);
    }

    /// <summary>Kept out of the screen reader's way: the line names the app.</summary>
    private sealed class Decoration(AppIcon owner) : FrameworkElementAutomationPeer(owner)
    {
        protected override bool IsControlElementCore() => false;

        protected override bool IsContentElementCore() => false;

        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Image;
    }
}
