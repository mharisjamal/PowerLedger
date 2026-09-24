using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace PowerLedger.App;

/// <summary>Send feedback: modeless, owned by the main window, single-instance like the household prompts, and fitting a
/// short screen the same way they do.</summary>
public partial class SendFeedbackWindow : Window
{
    private const double ScreenMargin = 24;

    private readonly FeedbackViewModel _model;
    private readonly Window? _mainWindow;
    private readonly IImagePicker _picker;

    internal SendFeedbackWindow(FeedbackViewModel model, Window? mainWindow, IImagePicker picker)
    {
        InitializeComponent();
        _model = model;
        _mainWindow = mainWindow;
        _picker = picker;
        DataContext = model;
        model.Closed += _ => Close();
        AllowDrop = true;
        Drop += OnDrop;
        MaxHeight = Math.Max(360, SystemParameters.WorkArea.Height - 2 * ScreenMargin);
        Loaded += (_, _) => KeepOnScreen();
    }

    private void KeepOnScreen()
    {
        var area = SystemParameters.WorkArea;
        Top = Math.Max(area.Top + ScreenMargin, Math.Min(Top, area.Bottom - ActualHeight - ScreenMargin));
    }

    private void AddImageClick(object sender, RoutedEventArgs e)
    {
        foreach (var path in _picker.Ask())
        {
            if (!_model.CanAddMoreImages) break;
            TryAddFile(path);
        }
    }

    private void AddScreenshotClick(object sender, RoutedEventArgs e)
    {
        if (_mainWindow is null || !_model.CanAddMoreImages) return;
        if (CaptureScreenshot(_mainWindow) is { } bytes) _model.TryAddImage(bytes);
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] paths) return;
        foreach (var path in paths)
        {
            if (!_model.CanAddMoreImages) break;
            TryAddFile(path);
        }
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);
        if (e.Key != Key.V || Keyboard.Modifiers != ModifierKeys.Control || !_model.CanAddMoreImages) return;
        if (Clipboard.GetImage() is not { } image) return;
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        _model.TryAddImage(stream.ToArray());
        e.Handled = true;
    }

    private void TryAddFile(string path)
    {
        try
        {
            _model.TryAddImage(File.ReadAllBytes(path));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // Nothing else to do with a file that can't be read; the row just doesn't gain one.
        }
    }

    /// <summary>The App's own window, not the whole screen: review round asked for "a PowerLedger screenshot"
    /// specifically. RenderTargetBitmap leaves out the OS window chrome, which is expected here, not a defect.</summary>
    private static byte[]? CaptureScreenshot(Window window)
    {
        try
        {
            var width = (int)Math.Max(1, window.ActualWidth);
            var height = (int)Math.Max(1, window.ActualHeight);
            var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(window);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var stream = new MemoryStream();
            encoder.Save(stream);
            return stream.ToArray();
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            return null;
        }
    }
}
